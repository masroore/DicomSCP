using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Imaging;
using DicomSCP.Configuration;
using DicomSCP.Models;
using Microsoft.Extensions.Options;
using System.Numerics;

namespace DicomSCP.Services;

public interface IPrintSCU
{
    Task<bool> PrintAsync(PrintRequest request);
    Task<bool> VerifyAsync(string hostName, int port, string calledAE);
}

public class PrintSCU : IPrintSCU
{
    private readonly DicomSettings _settings;
    private readonly string _aeTitle;

    // DICOM 打印常量
    private static class PrintConstants
    {
        // DICOM标准值
        public static readonly string[] PrintPriorities = { "HIGH", "MED", "LOW" };
        public static readonly string[] MediumTypes = { "PAPER", "CLEAR FILM", "BLUE FILM" };
        public static readonly string[] FilmDestinations = { "MAGAZINE", "PROCESSOR", "BIN_1", "BIN_2" };
        public static readonly string[] FilmOrientations = { "PORTRAIT", "LANDSCAPE" };
        public static readonly string[] FilmSizes = { 
            "8INX10IN", "10INX12IN", "11INX14IN", "14INX14IN", 
            "14INX17IN", "24CMX30CM", "A4" 
        };
        public static readonly string[] MagnificationTypes = { 
            "REPLICATE", "BILINEAR", "CUBIC", "NONE" 
        };
        public static readonly string[] SmoothingTypes = { 
            "NONE", "MEDIUM", "SMOOTH"  // 修正为标准值
        };
        public static readonly string[] Densities = { "BLACK", "WHITE" };
        public static readonly string[] TrimOptions = { "YES", "NO" };

        // 默认值 - 使用DICOM标准值
        public const string DefaultPriority = "MED";
        public const string DefaultMediumType = "BLUE FILM";
        public const string DefaultDestination = "PROCESSOR";
        public const string DefaultOrientation = "PORTRAIT";
        public const string DefaultSize = "14INX17IN";
        public const string DefaultDisplayFormat = "STANDARD\\1,1";
        public const string DefaultMagnification = "REPLICATE";
        public const string DefaultSmoothing = "MEDIUM";  // 修正为标准值
        public const string DefaultDensity = "BLACK";
        public const string DefaultTrim = "NO";
    }

    public PrintSCU(IOptions<DicomSettings> settings)
    {
        _settings = settings.Value;
        _aeTitle = _settings.PrintSCU?.AeTitle ?? "PRINTSCU";
    }

    private IDicomClient CreateClient(string hostName, int port, string callingAE, string calledAE)
    {
        var client = DicomClientFactory.Create(hostName, port, false, callingAE, calledAE);
        client.NegotiateAsyncOps();
        return client;
    }

    private bool ValidateAETitle(string aeTitle)
    {
        // AE Title 必须是 16 个字符以内的 ASCII 字符
        if (string.IsNullOrEmpty(aeTitle) || aeTitle.Length > 16)
            return false;

        // 只允许大写字母、数字和特定符号
        return aeTitle.All(c => (c >= 'A' && c <= 'Z') || 
                               (c >= '0' && c <= '9') || 
                               c == '-' || c == '_');
    }

    private bool ValidateHostName(string hostName)
    {
        // 检查是否为IP地址或有效的主机名
        return !string.IsNullOrEmpty(hostName) && 
               (System.Net.IPAddress.TryParse(hostName, out _) || 
                Uri.CheckHostName(hostName) != UriHostNameType.Unknown);
    }

    private bool ValidatePort(int port)
    {
        // 端口号范围：1-65535
        return port > 0 && port <= 65535;
    }

    private void SetDefaultValues(PrintRequest request)
    {
        if (request.NumberOfCopies < 1)
            request.NumberOfCopies = 1;
        if (string.IsNullOrEmpty(request.PrintPriority))
            request.PrintPriority = PrintConstants.DefaultPriority;
        if (string.IsNullOrEmpty(request.MediumType))
            request.MediumType = PrintConstants.DefaultMediumType;
        if (string.IsNullOrEmpty(request.FilmDestination))
            request.FilmDestination = PrintConstants.DefaultDestination;
        if (string.IsNullOrEmpty(request.FilmOrientation))
            request.FilmOrientation = PrintConstants.DefaultOrientation;
        if (string.IsNullOrEmpty(request.FilmSizeID))
            request.FilmSizeID = PrintConstants.DefaultSize;
        if (string.IsNullOrEmpty(request.ImageDisplayFormat))
            request.ImageDisplayFormat = PrintConstants.DefaultDisplayFormat;
        if (string.IsNullOrEmpty(request.MagnificationType))
            request.MagnificationType = PrintConstants.DefaultMagnification;
        if (string.IsNullOrEmpty(request.SmoothingType))
            request.SmoothingType = PrintConstants.DefaultSmoothing;
        if (string.IsNullOrEmpty(request.BorderDensity))
            request.BorderDensity = PrintConstants.DefaultDensity;
        if (string.IsNullOrEmpty(request.EmptyImageDensity))
            request.EmptyImageDensity = PrintConstants.DefaultDensity;
        if (string.IsNullOrEmpty(request.Trim))
            request.Trim = PrintConstants.DefaultTrim;
    }

    private string NormalizeSmoothingType(string? smoothing)
    {
        if (string.IsNullOrEmpty(smoothing))
            return PrintConstants.DefaultSmoothing;

        switch (smoothing.ToUpper().Trim())
        {
            case "NONE":
                return "NONE";
            case "MEDIUM":
            case "MED":
                return "MEDIUM";
            case "SMOOTH":
            case "HIGH":
                return "SMOOTH";
            default:
                return PrintConstants.DefaultSmoothing;
        }
    }

    private bool ValidateConnectionParameters(string hostName, int port, string calledAE, string? errorSource = null)
    {
        if (!ValidateAETitle(calledAE))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "无效的 Called AE Title: {CalledAE}", calledAE);
            return false;
        }

        if (!ValidateHostName(hostName))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "无效的主机名: {HostName}", hostName);
            return false;
        }

        if (!ValidatePort(port))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "无效的端口号: {Port}", port);
            return false;
        }

        return true;
    }

    private bool ValidatePrintParameters(PrintRequest request)
    {
        // 验证文件路径
        if (string.IsNullOrEmpty(request.FilePath))
        {
            DicomLogger.Error("PrintSCU", "文件路径不能为空");
            return false;
        }

        if (!System.IO.File.Exists(request.FilePath))
        {
            DicomLogger.Error("PrintSCU", "文件不存在: {FilePath}", request.FilePath);
            return false;
        }

        // 验证连接参数
        if (!ValidateConnectionParameters(request.HostName, request.Port, request.CalledAE))
            return false;

        // 标准化并验证打印优先级
        switch (request.PrintPriority?.ToUpper()?.Trim() ?? "")
        {
            case "HIGH":
            case "HI":
            case "H":
                request.PrintPriority = "HIGH";
                break;
            case "MEDIUM":
            case "MED":
            case "M":
                request.PrintPriority = "MED";
                break;
            case "LOW":
            case "LO":
            case "L":
                request.PrintPriority = "LOW";
                break;
            default:
                DicomLogger.Error("PrintSCU", "无效的打印优先级: {Priority}", 
                    request.PrintPriority ?? "NULL");
                return false;
        }

        // 标准化平滑类型
        request.SmoothingType = NormalizeSmoothingType(request.SmoothingType);

        // 验证打印参数
        if (!PrintConstants.MediumTypes.Contains(request.MediumType))
        {
            DicomLogger.Error("PrintSCU", "无效的类型: {MediumType}", request.MediumType);
            return false;
        }

        if (!PrintConstants.FilmOrientations.Contains(request.FilmOrientation))
        {
            DicomLogger.Error("PrintSCU", "无效的胶片方向: {Orientation}", request.FilmOrientation);
            return false;
        }

        if (!PrintConstants.FilmSizes.Contains(request.FilmSizeID))
        {
            DicomLogger.Error("PrintSCU", "无效的胶片尺寸: {Size}", request.FilmSizeID);
            return false;
        }

        if (!PrintConstants.MagnificationTypes.Contains(request.MagnificationType))
        {
            DicomLogger.Error("PrintSCU", "无效的放大类型: {Type}", request.MagnificationType);
            return false;
        }

        if (!PrintConstants.SmoothingTypes.Contains(request.SmoothingType))
        {
            DicomLogger.Error("PrintSCU", "无效的平滑类型: {Type}", request.SmoothingType);
            return false;
        }

        if (!PrintConstants.Densities.Contains(request.BorderDensity))
        {
            DicomLogger.Error("PrintSCU", "无效的边密度: {Density}", request.BorderDensity);
            return false;
        }

        if (!PrintConstants.Densities.Contains(request.EmptyImageDensity))
        {
            DicomLogger.Error("PrintSCU", "无效的空白密度: {Density}", request.EmptyImageDensity);
            return false;
        }

        if (!PrintConstants.TrimOptions.Contains(request.Trim))
        {
            DicomLogger.Error("PrintSCU", "无效的裁剪选项: {Trim}", request.Trim);
            return false;
        }

        return true;
    }

    private async Task<DicomFile> LoadDicomFileAsync(string filePath)
    {
        DicomLogger.Information("PrintSCU", "读取DICOM文件 - 路径: {FilePath}, 大小: {Size} bytes", 
            filePath, new FileInfo(filePath).Length);
        return await DicomFile.OpenAsync(filePath);
    }

    private DicomDataset CreateFilmSessionDataset(PrintRequest request)
    {
        return new DicomDataset
        {
            { DicomTag.NumberOfCopies, request.NumberOfCopies.ToString() },
            { DicomTag.PrintPriority, request.PrintPriority },
            { DicomTag.MediumType, request.MediumType },
            { DicomTag.FilmDestination, request.FilmDestination }
        };
    }

    private DicomDataset CreateFilmBoxDataset(PrintRequest request)
    {
        return new DicomDataset
        {
            { DicomTag.ImageDisplayFormat, request.ImageDisplayFormat },
            { DicomTag.FilmOrientation, request.FilmOrientation },
            { DicomTag.FilmSizeID, request.FilmSizeID },
            { DicomTag.MagnificationType, request.MagnificationType },
            { DicomTag.SmoothingType, request.SmoothingType },
            { DicomTag.BorderDensity, request.BorderDensity },
            { DicomTag.EmptyImageDensity, request.EmptyImageDensity },
            { DicomTag.Trim, request.Trim }
        };
    }

    private DicomDataset CreateImageBoxDataset(DicomFile file)
    {
        // 创建图像数据集
        var imageDataset = new DicomDataset
        {
            { DicomTag.Columns, (ushort)file.Dataset.GetSingleValue<int>(DicomTag.Columns) },
            { DicomTag.Rows, (ushort)file.Dataset.GetSingleValue<int>(DicomTag.Rows) },
            { DicomTag.BitsAllocated, (ushort)8 },
            { DicomTag.BitsStored, (ushort)8 },
            { DicomTag.HighBit, (ushort)7 },
            { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" }
        };

        // 转换图像
        var dicomImage = new DicomImage(file.Dataset);
        using var renderedImage = dicomImage.RenderImage();
        if (renderedImage is not IImage imageData)
        {
            throw new DicomDataException("图像转换失败");
        }

        var pixelData = new byte[imageData.Width * imageData.Height];
        ConvertToGrayscale(renderedImage, pixelData, imageData.Width, imageData.Height);
        imageDataset.Add(DicomTag.PixelData, pixelData);

        // 创建 Image Box 数据集
        return new DicomDataset
        {
            { DicomTag.ImageBoxPosition, (ushort)1 },
            { DicomTag.Polarity, "NORMAL" },
            { DicomTag.BasicGrayscaleImageSequence, new DicomDataset[] { imageDataset } }
        };
    }

    private static void ConvertToGrayscale(IImage renderedImage, byte[] pixelData, int width, int height)
    {
        var pixels = renderedImage.AsBytes();
        if (pixels == null || pixels.Length < width * height * 4)
        {
            throw new DicomDataException("图像数据获取失败");
        }

        // 使用 SIMD 优化的并行处理
        var vectorSize = Vector<byte>.Count;
        var vectorCount = pixels.Length / (4 * vectorSize);

        Parallel.For(0, vectorCount, i =>
        {
            var offset = i * 4 * vectorSize;
            for (var j = 0; j < vectorSize; j++)
            {
                var pixelOffset = offset + j * 4;
                var r = pixels[pixelOffset];
                var g = pixels[pixelOffset + 1];
                var b = pixels[pixelOffset + 2];
                pixelData[i * vectorSize + j] = (byte)((r * 38 + g * 75 + b * 15) >> 7);
            }
        });

        // 处理剩余的像素
        for (var i = vectorCount * vectorSize; i < width * height; i++)
        {
            var j = i * 4;
            pixelData[i] = (byte)((pixels[j] * 38 + pixels[j + 1] * 75 + pixels[j + 2] * 15) >> 7);
        }
    }

    public async Task<bool> VerifyAsync(string hostName, int port, string calledAE)
    {
        try
        {
            if (!ValidateConnectionParameters(hostName, port, calledAE, "VerifyAsync"))
                return false;

            var client = CreateClient(hostName, port, _aeTitle, calledAE);
            var echo = new DicomCEchoRequest();
            await client.AddRequestAsync(echo);
            await client.SendAsync();
            return true;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCU", ex, "验证连接时发生错误");
            return false;
        }
    }

    public async Task<bool> PrintAsync(PrintRequest request)
    {
        try
        {
            SetDefaultValues(request);
            if (!ValidatePrintParameters(request))
                return false;

            DicomLogger.Information("PrintSCU", "开始打印任务 - 从 {CallingAE} 到 {CalledAE}@{Host}:{Port}", 
                _aeTitle, request.CalledAE, request.HostName, request.Port);

            var file = await LoadDicomFileAsync(request.FilePath);

            var filmSessionUid = DicomUID.Generate();
            var filmBoxUid = DicomUID.Generate();
            var imageBoxUid = DicomUID.Generate();

            // 创建一个客户端用于打印过程
            var client = CreateClient(request.HostName, request.Port, _aeTitle, request.CalledAE);

            // 创建所有请求
            var allRequests = new List<DicomRequest>
            {
                // 1. 创建 Film Session
                new DicomNCreateRequest(DicomUID.BasicFilmSession, filmSessionUid)
                {
                    Dataset = CreateFilmSessionDataset(request)
                },

                // 2. 创建 Film Box
                new DicomNCreateRequest(DicomUID.BasicFilmBox, filmBoxUid)
                {
                    Dataset = CreateFilmBoxDataset(request)
                },

                // 3. 设置 Image Box
                new DicomNSetRequest(DicomUID.BasicGrayscaleImageBox, imageBoxUid)
                {
                    Dataset = CreateImageBoxDataset(file)
                },

                // 4. 打印操作
                new DicomNActionRequest(DicomUID.BasicFilmSession, filmSessionUid, 1)
            };

            // 发送所有请求
            DicomLogger.Information("PrintSCU", "开始发送打印请求序列");
            foreach (var req in allRequests)
            {
                await client.AddRequestAsync(req);
            }

            await client.SendAsync();
            DicomLogger.Information("PrintSCU", "打印任务完成");
            return true;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCU", ex, "打印过程中发生错误");
            return false;
        }
    }
} 

