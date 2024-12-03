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

    public static void Configure(
        DicomSettings settings,
        DicomRepository repository)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    private readonly string _printPath;

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

        _printPath = Path.Combine(_settings.StoragePath, "prints");
        Directory.CreateDirectory(_printPath);
    }

    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        DicomLogger.Information("PrintSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            association.CalledAE, association.CallingAE);

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification ||
                pc.AbstractSyntax == DicomUID.BasicFilmSession ||
                pc.AbstractSyntax == DicomUID.BasicFilmBox ||
                pc.AbstractSyntax == DicomUID.BasicGrayscaleImageBox ||
                pc.AbstractSyntax == DicomUID.BasicColorImageBox ||
                pc.AbstractSyntax == DicomUID.Printer ||
                pc.AbstractSyntax == DicomUID.PrinterConfigurationRetrieval ||
                pc.AbstractSyntax.UID == "1.2.840.10008.5.1.1.9" ||     // Basic Grayscale Print Management Meta SOP Class
                pc.AbstractSyntax.UID == "1.2.840.10008.5.1.1.18")      // Basic Color Print Management Meta SOP Class
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

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-CREATE 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

            if (request.SOPClassUID == DicomUID.BasicFilmSession)
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
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                if (_repository == null)
                {
                    throw new InvalidOperationException("PrintSCP repository not configured");
                }

                // 记录数据集中的所有标签
                var tags = request.Dataset.Select(item => item.Tag).ToList();
                DicomLogger.Information("PrintSCP", "胶片盒参数标签列表: {Tags}", 
                    string.Join(", ", tags.Select(t => t.ToString())));

                // 获取打印参数
                var filmBoxParams = new Dictionary<string, string>();

                // 获取胶片尺寸
                if (request.Dataset.TryGetSingleValue(DicomTag.FilmSizeID, out string filmSize))
                {
                    filmBoxParams["FilmSize"] = filmSize;
                }

                // 获取胶片方向
                if (request.Dataset.TryGetSingleValue(DicomTag.FilmOrientation, out string orientation))
                {
                    filmBoxParams["FilmOrientation"] = orientation;
                }

                // 获取图像显示格式
                if (request.Dataset.TryGetSingleValue(DicomTag.ImageDisplayFormat, out string layout))
                {
                    filmBoxParams["FilmLayout"] = layout;
                }

                // 获取放大类型
                if (request.Dataset.TryGetSingleValue(DicomTag.MagnificationType, out string magnification))
                {
                    filmBoxParams["MagnificationType"] = magnification;
                }

                // 获取边框密度
                if (request.Dataset.TryGetSingleValue(DicomTag.BorderDensity, out string borderDensity))
                {
                    filmBoxParams["BorderDensity"] = borderDensity;
                }

                // 获取空图像密度
                if (request.Dataset.TryGetSingleValue(DicomTag.EmptyImageDensity, out string emptyDensity))
                {
                    filmBoxParams["EmptyImageDensity"] = emptyDensity;
                }

                // 获取最小密度
                if (request.Dataset.TryGetSingleValue(DicomTag.MinDensity, out string minDensity))
                {
                    filmBoxParams["MinDensity"] = minDensity;
                }

                // 获取最大密度
                if (request.Dataset.TryGetSingleValue(DicomTag.MaxDensity, out string maxDensity))
                {
                    filmBoxParams["MaxDensity"] = maxDensity;
                }

                // 获取修剪
                if (request.Dataset.TryGetSingleValue(DicomTag.Trim, out string trim))
                {
                    filmBoxParams["Trim"] = trim;
                }

                // 获取配置信息
                if (request.Dataset.TryGetSingleValue(DicomTag.ConfigurationInformation, out string config))
                {
                    filmBoxParams["ConfigurationInfo"] = config;
                }

                // 记录打印参数
                DicomLogger.Information("PrintSCP", "胶片盒参数: {@Parameters}", filmBoxParams);

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
                }
                else
                {
                    DicomLogger.Warning("PrintSCP", "未找到关联的FilmSession");
                }
            }

            return new DicomNCreateResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-CREATE 请求失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-SET 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

            if (request.SOPClassUID == DicomUID.BasicGrayscaleImageBox ||
                request.SOPClassUID == DicomUID.BasicColorImageBox)
            {
                if (_repository == null)
                {
                    throw new InvalidOperationException("PrintSCP repository not configured");
                }

                // 记录数据集中的所有标签
                DicomLogger.Information("PrintSCP", "数据集标签列表: {Tags}", 
                    string.Join(", ", request.Dataset.Select(x => x.Tag.ToString())));

                // 检查图像数据
                var grayscaleImageTag = new DicomTag(0x2020, 0x0110);  // Basic Grayscale Image Sequence
                if (request.Dataset.Contains(grayscaleImageTag))
                {
                    var grayscaleSequence = request.Dataset.GetSequence(grayscaleImageTag);
                    if (grayscaleSequence != null && grayscaleSequence.Items.Count > 0)
                    {
                        DicomLogger.Information("PrintSCP", "找到图像序列 - 项目数: {Count}", grayscaleSequence.Items.Count);

                        foreach (var sequenceItem in grayscaleSequence.Items)
                        {
                            var sequenceItemTags = sequenceItem.Select(x => x.Tag).ToList();
                            DicomLogger.Information("PrintSCP", "图像序列项目标签: {Tags}", 
                                string.Join(", ", sequenceItemTags.Select(t => t.ToString())));

                            if (sequenceItem.Contains(DicomTag.PixelData))
                            {
                                // 创建基于日期的子目录
                                var dateFolder = DateTime.Now.ToString("yyyyMMdd");
                                var printFolder = Path.Combine(_printPath, dateFolder);
                                Directory.CreateDirectory(printFolder);

                                // 保存打印图像
                                var imagePath = Path.Combine(printFolder, $"{request.SOPInstanceUID.UID}.dcm");
                                DicomLogger.Information("PrintSCP", "开始保存打印图像 - 路径: {Path}", imagePath);

                                // 创建新的数据集并添加必要的元数据
                                var dataset = new DicomDataset();
                                
                                // 添加必要的元数据
                                dataset.Add(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
                                dataset.Add(DicomTag.SOPInstanceUID, DicomUID.Generate());
                                dataset.Add(DicomTag.StudyInstanceUID, DicomUID.Generate());
                                dataset.Add(DicomTag.SeriesInstanceUID, DicomUID.Generate());
                                dataset.Add(DicomTag.Modality, "OT");
                                dataset.Add(DicomTag.ConversionType, "WSD");

                                // 从原始数据集中提取患者信息
                                var patientId = sequenceItem.GetSingleValueOrDefault(DicomTag.PatientID, "UNKNOWN");
                                var patientName = sequenceItem.GetSingleValueOrDefault(DicomTag.PatientName, "UNKNOWN");
                                var accessionNumber = sequenceItem.GetSingleValueOrDefault(DicomTag.AccessionNumber, "");

                                // 尝试从FilmSession中查找最近的FilmBox
                                var filmBoxUid = "";
                                if (_repository != null)
                                {
                                    try
                                    {
                                        // 首先尝试从当前会话中获取FilmBox
                                        var filmSessionJobs = await _repository.GetPrintJobsByStatusAsync("PENDING");
                                        if (filmSessionJobs != null && filmSessionJobs.Any())
                                        {
                                            // 获取最近的打印任务
                                            var latestJob = filmSessionJobs.OrderByDescending(j => j.CreateTime).First();
                                            if (!string.IsNullOrEmpty(latestJob.FilmBoxId))
                                            {
                                                filmBoxUid = latestJob.FilmBoxId;
                                                DicomLogger.Information("PrintSCP", "使用当前会话的FilmBox - UID: {Uid}, FilmSessionId: {SessionId}", 
                                                    filmBoxUid, latestJob.FilmSessionId);

                                                // 更新打印任务中的患者信息和图像路径
                                                await _repository.UpdatePrintJobAsync(
                                                    filmSessionId: latestJob.FilmSessionId,
                                                    filmBoxId: filmBoxUid,
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
                                                    imagePath
                                                );

                                                DicomLogger.Information("PrintSCP", "更新打印任务成功 - JobId: {JobId}, FilmSessionId: {SessionId}", 
                                                    latestJob.JobId, latestJob.FilmSessionId);
                                            }
                                        }
                                        else
                                        {
                                            DicomLogger.Warning("PrintSCP", "未找到待处理的打印任务");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        DicomLogger.Warning("PrintSCP", ex, "获取待处理打印任务失败");
                                    }
                                }

                                // 如果没有找到关联的任务，创建新的打印任务
                                if (string.IsNullOrEmpty(filmBoxUid))
                                {
                                    try
                                    {
                                        var newJob = new PrintJob
                                        {
                                            JobId = Guid.NewGuid().ToString("N"),
                                            FilmSessionId = request.SOPInstanceUID.UID,  // 使用当前的SOP Instance UID作为FilmSession
                                            FilmBoxId = request.SOPInstanceUID.UID,      // 使用当前的SOP Instance UID作为FilmBox
                                            CallingAE = Association.CallingAE,
                                            Status = "PRINTING",
                                            ImagePath = imagePath,
                                            PatientId = patientId,
                                            PatientName = patientName,
                                            AccessionNumber = accessionNumber,
                                            CreateTime = DateTime.UtcNow
                                        };

                                        await _repository.AddPrintJobAsync(newJob);
                                        DicomLogger.Information("PrintSCP", "创建新的打印任务 - JobId: {JobId}, FilmSessionId: {SessionId}", 
                                            newJob.JobId, newJob.FilmSessionId);

                                        filmBoxUid = newJob.FilmBoxId;
                                    }
                                    catch (Exception ex)
                                    {
                                        DicomLogger.Error("PrintSCP", ex, "创建新的打印任务失败");
                                    }
                                }

                                // 添加患者信息到新的数据集
                                dataset.Add(DicomTag.PatientID, patientId);
                                dataset.Add(DicomTag.PatientName, patientName);
                                dataset.Add(DicomTag.AccessionNumber, accessionNumber);
                                dataset.Add(DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd"));
                                dataset.Add(DicomTag.StudyTime, DateTime.Now.ToString("HHmmss"));
                                dataset.Add(DicomTag.StudyID, "1");
                                dataset.Add(DicomTag.SeriesNumber, "1");
                                dataset.Add(DicomTag.InstanceNumber, "1");

                                // 复制图像相关的标签
                                foreach (var tag in sequenceItemTags)
                                {
                                    if (sequenceItem.Contains(tag))
                                    {
                                        dataset.Add(sequenceItem.GetDicomItem<DicomItem>(tag));
                                    }
                                }

                                // 创建DICOM文件并保存
                                var dicomFile = new DicomFile(dataset);
                                await dicomFile.SaveAsync(imagePath);

                                var fileInfo = new FileInfo(imagePath);
                                if (fileInfo.Exists && fileInfo.Length > 0)
                                {
                                    DicomLogger.Information("PrintSCP", 
                                        "打印图像保存成功 - 路径: {Path}, 大小: {Size:N0} 字节", 
                                        imagePath, 
                                        fileInfo.Length);

                                    return new DicomNSetResponse(request, DicomStatus.Success);
                                }
                            }
                        }
                    }
                }

                DicomLogger.Warning("PrintSCP", "打印图像数据中不包含像素数据");
                return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
            }

            return new DicomNSetResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-SET 请求失败");
            return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-ACTION 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                // 这里应该触发实际的打印操作
                DicomLogger.Information("PrintSCP", "触发打印操作 - 会话ID: {SessionId}", request.SOPInstanceUID.UID);

                if (_repository != null)
                {
                    // 更新打印任务状态为已完成
                    _repository.UpdatePrintJobStatusAsync(
                        request.SOPInstanceUID.UID,
                        "COMPLETED"
                    ).Wait();
                }
            }

            return Task.FromResult(new DicomNActionResponse(request, DicomStatus.Success));
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-ACTION 请求失败");
            return Task.FromResult(new DicomNActionResponse(request, DicomStatus.ProcessingFailure));
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