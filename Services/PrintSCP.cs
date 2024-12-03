using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Printing;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;
using Microsoft.Extensions.Options;

namespace DicomSCP.Services;

public class PrintSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private DicomSettings Settings => DicomServiceProvider.GetRequiredService<IOptions<DicomSettings>>().Value;
    private readonly DicomRepository _repository;
    private readonly string _printPath;
    private readonly string _relativePrintPath = "prints";

    // 添加缺失的字段
    private DicomFilmSession? _currentFilmSession;
    private DicomFilmBox? _currentFilmBox;
    private string? _callingAE;

    // 支持的传输语法
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    // 支持的SOP类
    private static readonly DicomUID[] SupportedSOPClasses = new[]
    {
        DicomUID.Verification,
        DicomUID.BasicFilmSession,
        DicomUID.BasicFilmBox,
        DicomUID.BasicGrayscaleImageBox,
        DicomUID.BasicColorImageBox,
        DicomUID.Printer,
        DicomUID.PrinterConfigurationRetrieval,
        DicomUID.Parse("1.2.840.10008.5.1.1.9"),     // Basic Grayscale Print Management Meta SOP Class
        DicomUID.Parse("1.2.840.10008.5.1.1.18")     // Basic Color Print Management Meta SOP Class
    };

    public PrintSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        try
        {
            _repository = DicomServiceProvider.GetRequiredService<DicomRepository>();
            _printPath = Path.Combine(Settings.StoragePath, _relativePrintPath);
            Directory.CreateDirectory(_printPath);
            DicomLogger.Information("PrintSCP", "PrintSCP服务初始化完成");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "PrintSCP服务初始化失败");
            throw;
        }
    }

    // 检查并创建打印目录
    private (string absolutePath, string relativePath) EnsurePrintDirectory()
    {
        var dateFolder = DateTime.Now.ToString("yyyyMMdd");
        var absolutePrintFolder = Path.Combine(_printPath, dateFolder);
        var relativePrintFolder = Path.Combine(_relativePrintPath, dateFolder);
        Directory.CreateDirectory(absolutePrintFolder);
        return (absolutePrintFolder, relativePrintFolder);
    }

    // 创建基本的DICOM数据集
    private DicomDataset CreateBasicDicomDataset()
    {
        var dataset = new DicomDataset();
        dataset.Add(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
        dataset.Add(DicomTag.SOPInstanceUID, DicomUID.Generate());
        dataset.Add(DicomTag.StudyInstanceUID, DicomUID.Generate());
        dataset.Add(DicomTag.SeriesInstanceUID, DicomUID.Generate());
        dataset.Add(DicomTag.Modality, "OT");
        dataset.Add(DicomTag.ConversionType, "WSD");
        dataset.Add(DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd"));
        dataset.Add(DicomTag.StudyTime, DateTime.Now.ToString("HHmmss"));
        dataset.Add(DicomTag.StudyID, "1");
        dataset.Add(DicomTag.SeriesNumber, "1");
        dataset.Add(DicomTag.InstanceNumber, "1");
        return dataset;
    }

    // 从数据集中提取打印参数
    private Dictionary<string, string> ExtractFilmBoxParameters(DicomDataset dataset)
    {
        var parameters = new Dictionary<string, string>();
        var parameterTags = new[]
        {
            (DicomTag.FilmSizeID, "FilmSize"),
            (DicomTag.FilmOrientation, "FilmOrientation"),
            (DicomTag.ImageDisplayFormat, "FilmLayout"),
            (DicomTag.MagnificationType, "MagnificationType"),
            (DicomTag.BorderDensity, "BorderDensity"),
            (DicomTag.EmptyImageDensity, "EmptyImageDensity"),
            (DicomTag.MinDensity, "MinDensity"),
            (DicomTag.MaxDensity, "MaxDensity"),
            (DicomTag.Trim, "Trim"),
            (DicomTag.ConfigurationInformation, "ConfigurationInfo")
        };

        foreach (var (tag, key) in parameterTags)
        {
            if (dataset.TryGetSingleValue(tag, out string value))
            {
                parameters[key] = value;
            }
        }

        return parameters;
    }

    // 记录数据集标签
    private void LogDatasetTags(DicomDataset dataset, string message)
    {
        var tags = dataset.Select(item => item.Tag).ToList();
        DicomLogger.Information("PrintSCP", message + ": {Tags}", 
            string.Join(", ", tags.Select(t => t.ToString())));
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            // 验证调用方 AE Title
            if (Settings.PrintSCP.ValidateCallingAE)
            {
                if (string.IsNullOrEmpty(association.CallingAE))
                {
                    DicomLogger.Warning("PrintSCP", "拒绝空的 Calling AE");
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }

                if (!Settings.PrintSCP.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
                {
                    DicomLogger.Warning("PrintSCP", "拒绝未授权的 Calling AE: {CallingAE}", association.CallingAE);
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }
            }

            foreach (var pc in association.PresentationContexts)
            {
                if (SupportedSOPClasses.Contains(pc.AbstractSyntax))
                {
                    pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                }
                else
                {
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理关联请求失败");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("PrintSCP", "接收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("PrintSCP", "接收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("PrintSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Information("PrintSCP", "连接正关闭");
        }
    }

    // 处理FilmSession创建请求
    private async Task<DicomStatus> HandleFilmSessionCreateAsync(DicomNCreateRequest request)
    {
        if (_repository == null)
        {
            throw new InvalidOperationException("PrintSCP repository not configured");
        }

        // 创建并保存 FilmSession
        _currentFilmSession = new DicomFilmSession(request.Dataset)
        {
            SOPInstanceUID = request.SOPInstanceUID.UID
        };
        _callingAE = Association.CallingAE;

        // 创建打印任务
        var job = new PrintJob
        {
            JobId = request.SOPInstanceUID.UID,  // 使用FilmSession的UID作为JobId
            FilmSessionId = request.SOPInstanceUID.UID,
            CallingAE = Association.CallingAE,
            Status = "PENDING",
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        };

        DicomLogger.Information("PrintSCP", 
            "创建打印会话 - JobId: {JobId}, 会话ID: {SessionId}, 来源: {CallingAE}",
            job.JobId,
            job.FilmSessionId,
            job.CallingAE);

        await _repository.AddPrintJobAsync(job);
        return DicomStatus.Success;
    }

    // 处理FilmBox创建请求
    private async Task<DicomStatus> HandleFilmBoxCreateAsync(DicomNCreateRequest request)
    {
        if (_repository == null)
        {
            throw new InvalidOperationException("PrintSCP repository not configured");
        }

        // 记录数据集中的所有标签
        LogDatasetTags(request.Dataset, "胶片盒参数标签列表");

        // 创建并保存 FilmBox
        _currentFilmBox = new DicomFilmBox(request.Dataset)
        {
            SOPInstanceUID = request.SOPInstanceUID.UID,
            FilmSizeID = request.Dataset.GetSingleValueOrDefault(DicomTag.FilmSizeID, ""),
            FilmOrientation = request.Dataset.GetSingleValueOrDefault(DicomTag.FilmOrientation, ""),
            ImageDisplayFormat = request.Dataset.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, ""),
            MagnificationType = request.Dataset.GetSingleValueOrDefault(DicomTag.MagnificationType, ""),
            BorderDensity = request.Dataset.GetSingleValueOrDefault(DicomTag.BorderDensity, ""),
            EmptyImageDensity = request.Dataset.GetSingleValueOrDefault(DicomTag.EmptyImageDensity, ""),
            MinDensity = request.Dataset.GetSingleValueOrDefault(DicomTag.MinDensity, ""),
            MaxDensity = request.Dataset.GetSingleValueOrDefault(DicomTag.MaxDensity, ""),
            Trim = request.Dataset.GetSingleValueOrDefault(DicomTag.Trim, ""),
            ConfigurationInformation = request.Dataset.GetSingleValueOrDefault(DicomTag.ConfigurationInformation, "")
        };

        // 获取关联的FilmSession
        var filmSessionUid = "";
        if (request.Dataset.Contains(DicomTag.ReferencedFilmSessionSequence))
        {
            var filmSessionSequence = request.Dataset.GetSequence(DicomTag.ReferencedFilmSessionSequence);
            if (filmSessionSequence != null && filmSessionSequence.Items.Count > 0)
            {
                filmSessionUid = filmSessionSequence.Items[0].GetSingleValueOrDefault<string>(DicomTag.ReferencedSOPInstanceUID, "");
            }
        }

        if (!string.IsNullOrEmpty(filmSessionUid))
        {
            // 更新打印任务，包括FilmBoxId和打印参数
            var filmBoxId = request.SOPInstanceUID.UID;
            DicomLogger.Information("PrintSCP", "更新打印任务 - FilmSessionId: {SessionId}, FilmBoxId: {BoxId}", 
                filmSessionUid, filmBoxId);

            await _repository.UpdatePrintJobAsync(
                filmSessionId: filmSessionUid,
                filmBoxId: filmBoxId,
                printParams: new Dictionary<string, string>
                {
                    { "FilmSize", _currentFilmBox.FilmSizeID },
                    { "FilmOrientation", _currentFilmBox.FilmOrientation },
                    { "FilmLayout", _currentFilmBox.ImageDisplayFormat },
                    { "MagnificationType", _currentFilmBox.MagnificationType },
                    { "BorderDensity", _currentFilmBox.BorderDensity },
                    { "EmptyImageDensity", _currentFilmBox.EmptyImageDensity },
                    { "MinDensity", _currentFilmBox.MinDensity },
                    { "MaxDensity", _currentFilmBox.MaxDensity },
                    { "TrimValue", _currentFilmBox.Trim },
                    { "ConfigurationInfo", _currentFilmBox.ConfigurationInformation }
                }
            );
            return DicomStatus.Success;
        }
        else
        {
            DicomLogger.Warning("PrintSCP", "未找到关联的FilmSession");
            return DicomStatus.InvalidObjectInstance;
        }
    }

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-CREATE 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

            var status = request.SOPClassUID switch
            {
                var uid when uid == DicomUID.BasicFilmSession => await HandleFilmSessionCreateAsync(request),
                var uid when uid == DicomUID.BasicFilmBox => await HandleFilmBoxCreateAsync(request),
                _ => DicomStatus.SOPClassNotSupported
            };

            return new DicomNCreateResponse(request, status);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-CREATE 请求失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    // 处理图像数据保存
    private async Task<(bool success, string? absolutePath, string? relativePath)> SaveImageDataAsync(DicomDataset imageItem, string sopInstanceUid)
    {
        try
        {
            if (!imageItem.Contains(DicomTag.PixelData))
            {
                return (false, null, null);
            }

            var (absolutePrintFolder, relativePrintFolder) = EnsurePrintDirectory();
            var fileName = $"{sopInstanceUid}.dcm";
            var absoluteImagePath = Path.Combine(absolutePrintFolder, fileName);
            var relativeImagePath = Path.Combine(relativePrintFolder, fileName);
            DicomLogger.Information("PrintSCP", "开始保存打印图像 - 路径: {Path}", absoluteImagePath);

            // 创建新的数据集并添加必要的元数据
            var dataset = CreateBasicDicomDataset();

            // 从原始数据集中提取患者信息
            var patientId = imageItem.GetSingleValueOrDefault(DicomTag.PatientID, "UNKNOWN");
            var patientName = imageItem.GetSingleValueOrDefault(DicomTag.PatientName, "UNKNOWN");
            var accessionNumber = imageItem.GetSingleValueOrDefault(DicomTag.AccessionNumber, "");

            // 添加患者信息到新的数据集
            dataset.Add(DicomTag.PatientID, patientId);
            dataset.Add(DicomTag.PatientName, patientName);
            dataset.Add(DicomTag.AccessionNumber, accessionNumber);

            // 复制图像相关的标签
            foreach (var tag in imageItem.Select(x => x.Tag))
            {
                if (imageItem.Contains(tag))
                {
                    dataset.Add(imageItem.GetDicomItem<DicomItem>(tag));
                }
            }

            // 创建DICOM文件并保存
            var dicomFile = new DicomFile(dataset);
            await dicomFile.SaveAsync(absoluteImagePath);

            var fileInfo = new FileInfo(absoluteImagePath);
            if (fileInfo.Exists && fileInfo.Length > 0)
            {
                DicomLogger.Information("PrintSCP", 
                    "打印图像保存成功 - 路径: {Path}, 大小: {Size:N0} 字节", 
                    absoluteImagePath, 
                    fileInfo.Length);
                return (true, absoluteImagePath, relativeImagePath);
            }

            return (false, null, null);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "保存打印图像失败");
            return (false, null, null);
        }
    }

    // 创建新的打印任务
    private async Task<PrintJob> CreateNewPrintJobAsync(
        string sopInstanceUid,
        string absoluteImagePath,
        string relativeImagePath,
        string patientId,
        string patientName,
        string accessionNumber)
    {
        if (_repository == null)
        {
            throw new InvalidOperationException("PrintSCP repository not configured");
        }

        var job = new PrintJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            FilmSessionId = _currentFilmSession?.SOPInstanceUID ?? "",
            FilmBoxId = _currentFilmBox?.SOPInstanceUID ?? "",
            CallingAE = _callingAE ?? "",
            Status = "PENDING",
            ImagePath = relativeImagePath,
            PatientId = patientId,
            PatientName = patientName,
            AccessionNumber = accessionNumber,
            FilmSize = _currentFilmBox?.FilmSizeID ?? "",
            FilmOrientation = _currentFilmBox?.FilmOrientation ?? "",
            FilmLayout = _currentFilmBox?.ImageDisplayFormat ?? "",
            MagnificationType = _currentFilmBox?.MagnificationType ?? "",
            BorderDensity = _currentFilmBox?.BorderDensity ?? "",
            EmptyImageDensity = _currentFilmBox?.EmptyImageDensity ?? "",
            MinDensity = _currentFilmBox?.MinDensity?.ToString() ?? "",
            MaxDensity = _currentFilmBox?.MaxDensity?.ToString() ?? "",
            TrimValue = _currentFilmBox?.Trim?.ToString() ?? "",
            ConfigurationInfo = _currentFilmBox?.ConfigurationInformation ?? "",
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        };

        await _repository.AddPrintJobAsync(job);
        return job;
    }

    // 处理图像盒设置请求
    private async Task<DicomStatus> HandleImageBoxSetAsync(DicomNSetRequest request)
    {
        try
        {
            if (_repository == null)
            {
                throw new InvalidOperationException("PrintSCP repository not configured");
            }

            // 记录数据集中的所有标签
            LogDatasetTags(request.Dataset, "数据集标签列表");

            // 检查图像数据
            var grayscaleImageTag = new DicomTag(0x2020, 0x0110);  // Basic Grayscale Image Sequence
            if (!request.Dataset.Contains(grayscaleImageTag))
            {
                DicomLogger.Warning("PrintSCP", "未找到图像序列");
                return DicomStatus.InvalidAttributeValue;
            }

            var grayscaleSequence = request.Dataset.GetSequence(grayscaleImageTag);
            if (grayscaleSequence == null || grayscaleSequence.Items.Count == 0)
            {
                DicomLogger.Warning("PrintSCP", "图像序列为空");
                return DicomStatus.InvalidAttributeValue;
            }

            DicomLogger.Information("PrintSCP", "找到图像序列 - 项目数: {Count}", grayscaleSequence.Items.Count);

            foreach (var sequenceItem in grayscaleSequence.Items)
            {
                // 记录图像序列中的标签
                LogDatasetTags(sequenceItem, "图像序列项目标签");

                // 存图像数据
                var (success, absoluteImagePath, relativeImagePath) = await SaveImageDataAsync(sequenceItem, request.SOPInstanceUID.UID);
                if (!success || absoluteImagePath == null || relativeImagePath == null)
                {
                    continue;
                }

                // 从原始数据集中提取患者信息
                var patientId = sequenceItem.GetSingleValueOrDefault(DicomTag.PatientID, "UNKNOWN");
                var patientName = sequenceItem.GetSingleValueOrDefault(DicomTag.PatientName, "UNKNOWN");
                var accessionNumber = sequenceItem.GetSingleValueOrDefault(DicomTag.AccessionNumber, "");

                try
                {
                    // 尝试从FilmSession中查找最近的打印任务
                    var pendingJobs = await _repository.GetPrintJobsByStatusAsync("PENDING");
                    var latestJob = pendingJobs?.OrderByDescending(j => j.CreateTime).FirstOrDefault();

                    if (latestJob != null)
                    {
                        // 更新现有打印任务
                        await _repository.UpdatePrintJobAsync(
                            filmSessionId: latestJob.FilmSessionId,
                            patientInfo: new Dictionary<string, string>
                            {
                                { "PatientId", patientId },
                                { "PatientName", patientName },
                                { "AccessionNumber", accessionNumber }
                            }
                        );

                        // 更新状态为COMPLETED并设置图像路径
                        await _repository.UpdatePrintJobStatusAsync(
                            latestJob.JobId,
                            "COMPLETED",  // 直接设置为已完成
                            relativeImagePath
                        );

                        DicomLogger.Information("PrintSCP", "打印任务完成 - JobId: {JobId}, FilmSessionId: {SessionId}, 图像路径: {Path}", 
                            latestJob.JobId, latestJob.FilmSessionId, absoluteImagePath);
                    }
                    else
                    {
                        // 如果没有找到待处理的任务，创建新的打印任务，并直接设置为已完成
                        var newJob = await CreateNewPrintJobAsync(
                            request.SOPInstanceUID.UID,
                            absoluteImagePath,
                            relativeImagePath,
                            patientId,
                            patientName,
                            accessionNumber
                        );

                        // 立即更新为已完成状态
                        await _repository.UpdatePrintJobStatusAsync(
                            newJob.JobId,
                            "COMPLETED"
                        );

                        DicomLogger.Information("PrintSCP", "创建并完成新的打印任务 - JobId: {JobId}, FilmSessionId: {SessionId}, 图像路径: {Path}", 
                            newJob.JobId, newJob.FilmSessionId, absoluteImagePath);
                    }

                    return DicomStatus.Success;
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("PrintSCP", ex, "处理打印任务失败");
                    return DicomStatus.ProcessingFailure;
                }
            }

            return DicomStatus.ProcessingFailure;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理图像盒设置请求失败");
            return DicomStatus.ProcessingFailure;
        }
    }

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-SET 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

            var status = request.SOPClassUID switch
            {
                var uid when uid == DicomUID.BasicGrayscaleImageBox => await HandleImageBoxSetAsync(request),
                var uid when uid == DicomUID.BasicColorImageBox => await HandleImageBoxSetAsync(request),
                _ => DicomStatus.SOPClassNotSupported
            };

            return new DicomNSetResponse(request, status);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-SET 请求失败");
            return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    // 处理打印操作请求
    private async Task<DicomStatus> HandlePrintActionAsync(DicomNActionRequest request)
    {
        try
        {
            if (_repository == null)
            {
                throw new InvalidOperationException("PrintSCP repository not configured");
            }

            DicomLogger.Information("PrintSCP", "触发打印操作 - 会话ID: {SessionId}", request.SOPInstanceUID.UID);

            // 更新打印任务状态为已完成
            await _repository.UpdatePrintJobStatusAsync(
                request.SOPInstanceUID.UID,
                "COMPLETED"
            );

            return DicomStatus.Success;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理打印操作失败");
            return DicomStatus.ProcessingFailure;
        }
    }

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-ACTION 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

            var status = request.SOPClassUID switch
            {
                var uid when uid == DicomUID.BasicFilmSession => await HandlePrintActionAsync(request),
                _ => DicomStatus.SOPClassNotSupported
            };

            return new DicomNActionResponse(request, status);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-ACTION 请求失败");
            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 N-DELETE 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);
        return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.Success));
    }

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 N-EVENT-REPORT 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 N-GET 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);
        return Task.FromResult(new DicomNGetResponse(request, DicomStatus.Success));
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }
}

// 添加辅助类
public class DicomFilmSession
{
    public DicomFilmSession(DicomDataset dataset)
    {
        // 可以从 dataset 中提取其他需要的属性
    }
    public string SOPInstanceUID { get; set; } = "";
}

public class DicomFilmBox
{
    public DicomFilmBox(DicomDataset dataset)
    {
        // 可以从 dataset 中提取其他需要的属性
    }
    public string SOPInstanceUID { get; set; } = "";
    public string FilmSizeID { get; set; } = "";
    public string FilmOrientation { get; set; } = "";
    public string ImageDisplayFormat { get; set; } = "";
    public string MagnificationType { get; set; } = "";
    public string BorderDensity { get; set; } = "";
    public string EmptyImageDensity { get; set; } = "";
    public string MinDensity { get; set; } = "";
    public string MaxDensity { get; set; } = "";
    public string Trim { get; set; } = "";
    public string ConfigurationInformation { get; set; } = "";
} 