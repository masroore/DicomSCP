using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Printing;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;

namespace DicomSCP.Services;

public class PrintSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private static DicomSettings? _settings;
    private static DicomRepository? _repository;
    private readonly string _printPath;
    private readonly string _relativePrintPath = "prints";

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

    public static void Configure(
        DicomSettings settings,
        DicomRepository repository)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public PrintSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        if (_settings == null || _repository == null)
        {
            throw new InvalidOperationException("PrintSCP not configured");
        }

        _printPath = Path.Combine(_settings.StoragePath, _relativePrintPath);
        Directory.CreateDirectory(_printPath);
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
        DicomLogger.Information("PrintSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            association.CalledAE, association.CallingAE);

        foreach (var pc in association.PresentationContexts)
        {
            if (SupportedSOPClasses.Contains(pc.AbstractSyntax))
            {
                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                DicomLogger.Information("PrintSCP", "接受打印服务 - {Service}", pc.AbstractSyntax.Name);
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                DicomLogger.Warning("PrintSCP", "拒绝不支持的服务 - {Service}", pc.AbstractSyntax.Name);
            }
        }

        return SendAssociationAcceptAsync(association);
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

        // 创建打印任务
        var job = new PrintJob
        {
            JobId = request.SOPInstanceUID.UID,  // 使用FilmSession的UID作为JobId
            FilmSessionId = request.SOPInstanceUID.UID,
            CallingAE = Association.CallingAE,
            Status = "PENDING",
            CreateTime = DateTime.UtcNow
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

        // 获取打印参数
        var filmBoxParams = ExtractFilmBoxParameters(request.Dataset);
        DicomLogger.Information("PrintSCP", "片盒参数: {@Parameters}", filmBoxParams);

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
            // 更新打印任务，包括FilmBoxId
            var filmBoxId = request.SOPInstanceUID.UID;
            DicomLogger.Information("PrintSCP", "更新打印任务 - FilmSessionId: {SessionId}, FilmBoxId: {BoxId}", 
                filmSessionUid, filmBoxId);

            await _repository.UpdatePrintJobAsync(
                filmSessionId: filmSessionUid,
                filmBoxId: filmBoxId,
                printParams: filmBoxParams
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
        var newJob = new PrintJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            FilmSessionId = sopInstanceUid,
            FilmBoxId = sopInstanceUid,
            CallingAE = Association.CallingAE,
            Status = "PRINTING",
            ImagePath = relativeImagePath,
            PatientId = patientId,
            PatientName = patientName,
            AccessionNumber = accessionNumber,
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        };

        await _repository!.AddPrintJobAsync(newJob);
        DicomLogger.Information("PrintSCP", "创建新的打印任务 - JobId: {JobId}, FilmSessionId: {SessionId}, 图像路径: {Path}", 
            newJob.JobId, newJob.FilmSessionId, absoluteImagePath);

        return newJob;
    }

    // 处理图像盒设置请求
    private async Task<DicomStatus> HandleImageBoxSetAsync(DicomNSetRequest request)
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

            // 保存图像数据
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

                    await _repository.UpdatePrintJobStatusAsync(
                        latestJob.JobId,
                        "PRINTING",
                        relativeImagePath
                    );

                    DicomLogger.Information("PrintSCP", "更新打印任务成功 - JobId: {JobId}, FilmSessionId: {SessionId}, 图像路径: {Path}", 
                        latestJob.JobId, latestJob.FilmSessionId, absoluteImagePath);
                }
                else
                {
                    // 如果没有找到待处理的任务，创建新的打印任务
                    var newJob = await CreateNewPrintJobAsync(
                        request.SOPInstanceUID.UID,
                        absoluteImagePath,
                        relativeImagePath,
                        patientId,
                        patientName,
                        accessionNumber
                    );

                    DicomLogger.Information("PrintSCP", "创建新的打印任务成功 - JobId: {JobId}, FilmSessionId: {SessionId}, 图像路径: {Path}", 
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