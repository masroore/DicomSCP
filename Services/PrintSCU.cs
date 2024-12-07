using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Imaging;
using DicomSCP.Configuration;
using DicomSCP.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using static DicomSCP.Models.PrintJob;

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
    private readonly ILoggerFactory _loggerFactory;

    // DICOM 打印常量
    private static class PrintConstants
    {
        public static readonly string[] PrintPriorities = { "HIGH", "MEDIUM", "LOW" };
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
            "NONE", "LOW", "MEDIUM", "HIGH" 
        };
        public static readonly string[] Densities = { "BLACK", "WHITE" };
        public static readonly string[] TrimOptions = { "YES", "NO" };

        // 默认值
        public const string DefaultPriority = "MEDIUM";
        public const string DefaultMediumType = "BLUE FILM";
        public const string DefaultDestination = "PROCESSOR";
        public const string DefaultOrientation = "PORTRAIT";
        public const string DefaultSize = "14INX17IN";
        public const string DefaultDisplayFormat = "STANDARD\\1,1";
        public const string DefaultMagnification = "REPLICATE";
        public const string DefaultSmoothing = "MEDIUM";
        public const string DefaultDensity = "WHITE";
        public const string DefaultTrim = "NO";
    }

    public PrintSCU(IOptions<DicomSettings> settings, ILoggerFactory loggerFactory)
    {
        _settings = settings.Value;
        _aeTitle = _settings.PrintSCU?.AeTitle ?? "PRINTSCU";
        _loggerFactory = loggerFactory;
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
            request.PrintPriority = "MEDIUM";
        if (string.IsNullOrEmpty(request.MediumType))
            request.MediumType = "BLUE FILM";
        if (string.IsNullOrEmpty(request.FilmDestination))
            request.FilmDestination = "PROCESSOR";
        if (string.IsNullOrEmpty(request.FilmOrientation))
            request.FilmOrientation = "PORTRAIT";
        if (string.IsNullOrEmpty(request.FilmSizeID))
            request.FilmSizeID = "14INX17IN";
        if (string.IsNullOrEmpty(request.ImageDisplayFormat))
            request.ImageDisplayFormat = "STANDARD\\1,1";
        if (string.IsNullOrEmpty(request.MagnificationType))
            request.MagnificationType = "REPLICATE";
        if (string.IsNullOrEmpty(request.SmoothingType))
            request.SmoothingType = "MEDIUM";
        if (string.IsNullOrEmpty(request.BorderDensity))
            request.BorderDensity = "BLACK";
        if (string.IsNullOrEmpty(request.EmptyImageDensity))
            request.EmptyImageDensity = "BLACK";
        if (string.IsNullOrEmpty(request.Trim))
            request.Trim = "NO";
        if (request.EnableDpi && !request.Dpi.HasValue)
        {
            request.Dpi = 150;  // 设置默认DPI为150
        }
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

        // 验证打印机连接参数
        if (!ValidateAETitle(request.CalledAE))
        {
            DicomLogger.Error("PrintSCU", "无效的 Called AE Title: {CalledAE}", request.CalledAE);
            return false;
        }

        if (!ValidateHostName(request.HostName))
        {
            DicomLogger.Error("PrintSCU", "无效的主机名: {HostName}", request.HostName);
            return false;
        }

        if (!ValidatePort(request.Port))
        {
            DicomLogger.Error("PrintSCU", "无效的端口号: {Port}", request.Port);
            return false;
        }

        // 验证打印参数
        if (!PrintConstants.PrintPriorities.Contains(request.PrintPriority))
        {
            DicomLogger.Error("PrintSCU", "无效的打印优先级: {Priority}", request.PrintPriority);
            return false;
        }

        if (!PrintConstants.MediumTypes.Contains(request.MediumType))
        {
            DicomLogger.Error("PrintSCU", "无效的类型: {MediumType}", request.MediumType);
            return false;
        }

        // 补充其他打印参数的验证
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
            DicomLogger.Error("PrintSCU", "无效��边密度: {Density}", request.BorderDensity);
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

        // 验证DPI设置
        if (request.EnableDpi)
        {
            if (!request.Dpi.HasValue)
            {
                DicomLogger.Error("PrintSCU", "启用DPI时必须指定DPI值");
                return false;
            }
            if (request.Dpi.Value < 100 || request.Dpi.Value > 300)
            {
                DicomLogger.Error("PrintSCU", "无效的DPI值: {DPI}，有效范围: 100-300", request.Dpi.Value);
                return false;
            }
        }

        return true;
    }

    private async Task<DicomFile> LoadDicomFileAsync(string filePath)
    {
        DicomLogger.Information("PrintSCU", "读取DICOM文件 - 路径: {FilePath}, 大小: {Size} bytes", 
            filePath, new FileInfo(filePath).Length);
        return await DicomFile.OpenAsync(filePath);
    }

    private void AddPresentationContexts(IDicomClient client)
    {
        client.AdditionalPresentationContexts.Add(
            DicomPresentationContext.GetScpRolePresentationContext(
                DicomUID.BasicFilmSession));
        client.AdditionalPresentationContexts.Add(
            DicomPresentationContext.GetScpRolePresentationContext(
                DicomUID.BasicFilmBox));
        client.AdditionalPresentationContexts.Add(
            DicomPresentationContext.GetScpRolePresentationContext(
                DicomUID.BasicGrayscaleImageBox));
    }

    private DicomDataset CreateFilmSessionDataset(PrintRequest request, DicomUID filmSessionUid)
    {
        var dataset = new DicomDataset();
        dataset.AddOrUpdate(DicomTag.NumberOfCopies, request.NumberOfCopies.ToString());
        dataset.AddOrUpdate(DicomTag.PrintPriority, request.PrintPriority);
        dataset.AddOrUpdate(DicomTag.MediumType, request.MediumType);
        dataset.AddOrUpdate(DicomTag.FilmDestination, request.FilmDestination);
        return dataset;
    }

    private DicomDataset CreateFilmBoxDataset(PrintRequest request)
    {
        var dataset = new DicomDataset();
        dataset.AddOrUpdate(DicomTag.ImageDisplayFormat, request.ImageDisplayFormat);
        dataset.AddOrUpdate(DicomTag.FilmOrientation, request.FilmOrientation);
        dataset.AddOrUpdate(DicomTag.FilmSizeID, request.FilmSizeID);
        dataset.AddOrUpdate(DicomTag.MagnificationType, request.MagnificationType);
        dataset.AddOrUpdate(DicomTag.SmoothingType, request.SmoothingType);
        dataset.AddOrUpdate(DicomTag.BorderDensity, request.BorderDensity);
        dataset.AddOrUpdate(DicomTag.EmptyImageDensity, request.EmptyImageDensity);
        dataset.AddOrUpdate(DicomTag.Trim, request.Trim);
        return dataset;
    }

    private DicomDataset CreateImageBoxDataset(DicomFile file)
    {
        var dataset = new DicomDataset();

        // 设置基本图像参数
        dataset.Add(DicomTag.Columns, (ushort)file.Dataset.GetSingleValue<int>(DicomTag.Columns));
        dataset.Add(DicomTag.Rows, (ushort)file.Dataset.GetSingleValue<int>(DicomTag.Rows));
        dataset.Add(DicomTag.BitsAllocated, (ushort)8);
        dataset.Add(DicomTag.BitsStored, (ushort)8);
        dataset.Add(DicomTag.HighBit, (ushort)7);
        dataset.Add(DicomTag.PixelRepresentation, (ushort)0);
        dataset.Add(DicomTag.SamplesPerPixel, (ushort)1);
        dataset.Add(DicomTag.PhotometricInterpretation, "MONOCHROME2");

        // 复制检查号等标识信息
        if (file.Dataset.Contains(DicomTag.AccessionNumber))
        {
            dataset.Add(DicomTag.AccessionNumber, file.Dataset.GetString(DicomTag.AccessionNumber));
            DicomLogger.Debug("PrintSCU", "复制检查号: {AccNo}", file.Dataset.GetString(DicomTag.AccessionNumber));
        }

        // 复制 StudyInstanceUID
        if (file.Dataset.Contains(DicomTag.StudyInstanceUID))
        {
            dataset.Add(DicomTag.StudyInstanceUID, file.Dataset.GetString(DicomTag.StudyInstanceUID));
            DicomLogger.Debug("PrintSCU", "复制检查 UID: {StudyUID}", file.Dataset.GetString(DicomTag.StudyInstanceUID));
        }
        else
        {
            // 如果没有 StudyInstanceUID，生成一个新的
            var studyUid = DicomUID.Generate();
            dataset.Add(DicomTag.StudyInstanceUID, studyUid.UID);
            DicomLogger.Debug("PrintSCU", "生成新的检查 UID: {StudyUID}", studyUid.UID);
        }

        // 复制其他相关标识信息
        if (file.Dataset.Contains(DicomTag.PatientID))
        {
            dataset.Add(DicomTag.PatientID, file.Dataset.GetString(DicomTag.PatientID));
        }
        if (file.Dataset.Contains(DicomTag.PatientName))
        {
            dataset.Add(DicomTag.PatientName, file.Dataset.GetString(DicomTag.PatientName));
        }

        // 转换图像
        var dicomImage = new DicomImage(file.Dataset);
        using var renderedImage = dicomImage.RenderImage();
        if (renderedImage is not IImage imageData)
        {
            throw new DicomDataException("图像转换失败");
        }

        var pixelData = new byte[imageData.Width * imageData.Height];
        ConvertToGrayscale(renderedImage, pixelData, imageData.Width, imageData.Height);
        dataset.Add(DicomTag.PixelData, pixelData);
        
        DicomLogger.Information("PrintSCU", "已转换为8位灰度图像");

        return dataset;
    }

    private static void ConvertToGrayscale(IImage renderedImage, byte[] pixelData, int width, int height)
    {
        try
        {
            var pixels = renderedImage.AsBytes();
            if (pixels == null || pixels.Length < width * height * 4)
            {
                throw new DicomDataException("图像数据获取失败");
            }

            // 使用并行处理来提高性能
            Parallel.For(0, height * width, j =>
            {
                var i = j * 4;
                // R * 38 + G * 75 + B * 15 >> 7 等价于 R * 0.3 + G * 0.59 + B * 0.11
                pixelData[j] = (byte)((pixels[i] * 38 + pixels[i + 1] * 75 + pixels[i + 2] * 15) >> 7);
            });

            DicomLogger.Information("PrintSCU", "已转换 {Count} 个像素为灰度值", width * height);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCU", ex, "图像转换失败");
            throw new DicomDataException("图像转换失败: " + ex.Message);
        }
    }

    public async Task<bool> VerifyAsync(string hostName, int port, string calledAE)
    {
        try
        {
            // 验证参数
            if (!ValidateAETitle(calledAE))
            {
                DicomLogger.Warning("PrintSCU", "无效的 Called AE Title: {CalledAE}", calledAE);
                return false;
            }

            if (!ValidateHostName(hostName))
            {
                DicomLogger.Warning("PrintSCU", "无效的主机名: {HostName}", hostName);
                return false;
            }

            if (!ValidatePort(port))
            {
                DicomLogger.Warning("PrintSCU", "无效的端口号: {Port}", port);
                return false;
            }

            var client = CreateClient(hostName, port, _aeTitle, calledAE);
            var echo = new DicomCEchoRequest();
            await client.AddRequestAsync(echo);
            await client.SendAsync();
            return true;
        }
        catch (DicomAssociationRejectedException ex)
        {
            // 处理连接被拒绝的情况
            DicomLogger.Warning("PrintSCU", 
                "打印机拒连接 - {CalledAE}@{Host}:{Port}, 原因: {Reason}", 
                calledAE, hostName, port, ex.RejectResult);
            return false;
        }
        catch (Exception ex)
        {
            // 处理其他错误
            DicomLogger.Warning("PrintSCU", 
                "连接打印机失败 - {CalledAE}@{Host}:{Port}, 错误: {Error}", 
                calledAE, hostName, port, ex.Message);
            return false;
        }
    }

    public async Task<bool> PrintAsync(PrintRequest request)
    {
        try
        {
            // 设置默认值和验证
            SetDefaultValues(request);
            if (!ValidatePrintParameters(request))
                return false;

            // 验证连接
            if (!await VerifyAsync(request.HostName, request.Port, request.CalledAE))
            {
                DicomLogger.Error("PrintSCU", "打印连接验证失败");
                return false;
            }

            DicomLogger.Information("PrintSCU", "开始打印任务 - 从 {CallingAE} 到 {CalledAE}@{Host}:{Port}", 
                _aeTitle, request.CalledAE, request.HostName, request.Port);

            // 读取源文件
            var file = await LoadDicomFileAsync(request.FilePath);

            // 如果设置了DPI，处理图像
            DicomDataset imageDataset;
            if (request.EnableDpi && request.Dpi.HasValue)
            {
                DicomLogger.Information("PrintSCU", "使用DPI处理图像: {DPI}", request.Dpi.Value);
                imageDataset = ImageProcessor.ResizeImage(file, request.Dpi.Value, request.FilmSizeID);
            }
            else
            {
                DicomLogger.Information("PrintSCU", "不使用DPI处理，保持原始图像尺寸");
                imageDataset = file.Dataset;
            }

            // 创建打印客户端
            var client = CreateClient(request.HostName, request.Port, _aeTitle, request.CalledAE);
            AddPresentationContexts(client);

            // 生成UID
            var filmSessionUid = DicomUID.Generate();
            var filmBoxUid = DicomUID.Generate();
            var imageBoxUid = DicomUID.Generate();

            // 创建请求
            var filmSessionRequest = new DicomNCreateRequest(DicomUID.BasicFilmSession, filmSessionUid)
            {
                Dataset = CreateFilmSessionDataset(request, filmSessionUid)
            };

            var filmBoxRequest = new DicomNCreateRequest(DicomUID.BasicFilmBox, filmBoxUid)
            {
                Dataset = CreateFilmBoxDataset(request)
            };

            var imageBoxRequest = new DicomNSetRequest(DicomUID.BasicGrayscaleImageBox, imageBoxUid)
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.ImageBoxPosition, (ushort)1 },
                    { DicomTag.Polarity, "NORMAL" },
                    { DicomTag.ImageBoxNumber, (ushort)1 }
                }
            };

            // 添加图像序列
            var imageSequence = new DicomSequence(DicomTag.BasicGrayscaleImageSequence);
            imageSequence.Items.Add(CreateImageBoxDataset(new DicomFile(imageDataset)));
            imageBoxRequest.Dataset.Add(imageSequence);

            // 发送请求序列
            DicomLogger.Information("PrintSCU", "开始发送打印请求序列");
            await client.AddRequestAsync(filmSessionRequest);
            await client.AddRequestAsync(filmBoxRequest);
            await client.AddRequestAsync(imageBoxRequest);
            await client.AddRequestAsync(new DicomNActionRequest(DicomUID.BasicFilmSession, filmSessionUid, 1));

            await client.SendAsync();
            DicomLogger.Information("PrintSCU", "打印请求已发送，开始清理资源");

            // 清理资源
            try 
            {
                // 删除 Film Box
                var deleteFilmBoxRequest = new DicomNDeleteRequest(DicomUID.BasicFilmBox, filmBoxUid);
                await client.AddRequestAsync(deleteFilmBoxRequest);

                // 删除 Film Session
                var deleteFilmSessionRequest = new DicomNDeleteRequest(DicomUID.BasicFilmSession, filmSessionUid);
                await client.AddRequestAsync(deleteFilmSessionRequest);

                await client.SendAsync();
                DicomLogger.Information("PrintSCU", "资源清理完成");
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("PrintSCU", ex, "清理资源时发生错误，但不影响打印");
            }

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

