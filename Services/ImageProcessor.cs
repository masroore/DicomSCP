using FellowOakDicom;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using FellowOakDicom.Imaging;

namespace DicomSCP.Services;

public static class ImageProcessor
{
    private static readonly Dictionary<string, (float width, float height)> FilmSizes = new()
    {
        ["14INX17IN"] = (14.0f, 17.0f),
        ["11INX14IN"] = (11.0f, 14.0f),
        ["8INX10IN"] = (8.0f, 10.0f),
        ["A4"] = (8.27f, 11.69f)
    };

    public static DicomDataset ResizeImage(DicomFile file, int dpi, string filmSizeID)
    {
        // 获取胶片物理尺寸
        if (!FilmSizes.TryGetValue(filmSizeID, out var size))
        {
            throw new ArgumentException($"不支持的胶片尺寸: {filmSizeID}");
        }

        // 计算目标像素尺寸
        int targetWidth = (int)(size.width * dpi);
        int targetHeight = (int)(size.height * dpi);

        // 创建新的数据集
        var processedDataset = new DicomDataset();

        // 复制原始图像属性
        foreach (var element in file.Dataset)
        {
            if (element.Tag != DicomTag.PixelData &&
                element.Tag != DicomTag.Columns &&
                element.Tag != DicomTag.Rows)
            {
                processedDataset.Add(element);
            }
        }

        // 调整图像尺寸
        var image = new DicomImage(file.Dataset);
        var renderedImage = image.RenderImage();
        
        using var memoryStream = new MemoryStream();
        using (var outputImage = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            renderedImage.AsBytes(), 
            renderedImage.Width, 
            renderedImage.Height))
        {
            outputImage.Mutate(x => x.Resize(targetWidth, targetHeight));
            outputImage.Mutate(x => x.Grayscale());
            outputImage.SaveAsPng(memoryStream);
        }

        // 设置新的图像参数
        processedDataset.Add(DicomTag.Columns, (ushort)targetWidth);
        processedDataset.Add(DicomTag.Rows, (ushort)targetHeight);
        processedDataset.Add(DicomTag.BitsAllocated, (ushort)8);
        processedDataset.Add(DicomTag.BitsStored, (ushort)8);
        processedDataset.Add(DicomTag.HighBit, (ushort)7);
        processedDataset.Add(DicomTag.PixelRepresentation, (ushort)0);
        processedDataset.Add(DicomTag.SamplesPerPixel, (ushort)1);
        processedDataset.Add(DicomTag.PhotometricInterpretation, "MONOCHROME2");
        processedDataset.Add(DicomTag.PixelData, memoryStream.ToArray());

        return processedDataset;
    }
} 