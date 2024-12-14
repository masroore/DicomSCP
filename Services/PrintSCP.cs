using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;
using Microsoft.Extensions.Options;

namespace DicomSCP.Services;

public class PrintSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private readonly string _printPath;
    private readonly string _relativePrintPath = "prints";
    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;

    // 会话状态管理
    private class PrintSession
    {
        public DicomFilmSession? FilmSession { get; set; }
        public DicomFilmBox? CurrentFilmBox { get; set; }
        public string? CallingAE { get; set; }
        public DateTime CreatedTime { get; set; } = DateTime.Now;
    }

    private PrintSession _session = new();

    // 支持的传输语法
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    public PrintSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _settings = DicomServiceProvider.GetRequiredService<IOptions<DicomSettings>>().Value;
        _repository = DicomServiceProvider.GetRequiredService<DicomRepository>();
        _printPath = Path.Combine(_settings.StoragePath, _relativePrintPath);
        Directory.CreateDirectory(_printPath);
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                association.CalledAE, 
                association.CallingAE);

            // AE Title 验证
            if (_settings.PrintSCP.ValidateCallingAE && 
                !_settings.PrintSCP.AllowedCallingAEs.Contains(association.CallingAE))
            {
                DicomLogger.Warning("PrintSCP", "拒绝未授权的 Calling AE: {CallingAE}", association.CallingAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }

            if (_settings.PrintSCP.AeTitle != association.CalledAE)
            {
                DicomLogger.Warning("PrintSCP", "拒绝错误的 Called AE: {CalledAE}，期望：{ExpectedAE}", 
                    association.CalledAE, 
                    _settings.PrintSCP.AeTitle);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            _session.CallingAE = association.CallingAE;

            // 定义支持的服务类型
            var supportedSOPClasses = new DicomUID[]
            {
                DicomUID.BasicFilmSession,
                DicomUID.BasicFilmBox,
                DicomUID.BasicGrayscalePrintManagementMeta,
                DicomUID.BasicColorPrintManagementMeta,
                DicomUID.BasicGrayscaleImageBox,
                DicomUID.BasicColorImageBox,
                DicomUID.Verification  // C-ECHO
            };

            var hasValidPresentationContext = false;

            foreach (var pc in association.PresentationContexts)
            {
                DicomLogger.Information("PrintSCP", "处理表示上下文 - Abstract Syntax：{AbstractSyntax}", 
                    pc.AbstractSyntax.Name);

                if (!supportedSOPClasses.Contains(pc.AbstractSyntax))
                {
                    DicomLogger.Warning("PrintSCP", "不支持的服务类型：{AbstractSyntax}", 
                        pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                    continue;
                }

                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                hasValidPresentationContext = true;
            }

            if (!hasValidPresentationContext)
            {
                DicomLogger.Warning("PrintSCP", "没有有效的表示上下文，拒绝关联请求");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.ApplicationContextNotSupported);
            }

            DicomLogger.Information("PrintSCP", "接受关联请求");
            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理关联请求时发生错误");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.ApplicationContextNotSupported);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("PrintSCP", "收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("PrintSCP", "收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        try
        {
            if (exception != null)
            {
                DicomLogger.Error("PrintSCP", exception, "连接异常关闭");
            }
            else
            {
                DicomLogger.Information("PrintSCP", "连接正常关闭");
            }

            CleanupSession();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "清理资源时发生错误");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-CREATE 请求 - SOP Class: {SopClass}, Calling AE: {CallingAE}", 
                request.SOPClassUID?.Name ?? "Unknown", 
                _session.CallingAE ?? "Unknown");

            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                return await HandleFilmSessionCreateAsync(request);
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                return await HandleFilmBoxCreateAsync(request);
            }

            return new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-CREATE 请求失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNCreateResponse> HandleFilmSessionCreateAsync(DicomNCreateRequest request)
    {
        try
        {
            // 创建打印作业
            var printJob = new PrintJob
            {
                JobId = $"{DateTime.Now:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}",
                FilmSessionId = request.SOPInstanceUID.UID,
                CallingAE = _session.CallingAE ?? "",
                Status = PrintJobStatus.Created,
                
                // Film Session 参数
                NumberOfCopies = request.Dataset?.GetSingleValueOrDefault(DicomTag.NumberOfCopies, 1) ?? 1,
                PrintPriority = request.Dataset?.GetSingleValueOrDefault(DicomTag.PrintPriority, "LOW") ?? "LOW",
                MediumType = request.Dataset?.GetSingleValueOrDefault(DicomTag.MediumType, "BLUE FILM") ?? "BLUE FILM",
                FilmDestination = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmDestination, "MAGAZINE") ?? "MAGAZINE",
                
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };

            // 保存到数据库
            await _repository.AddPrintJobAsync(printJob);

            var response = new DicomNCreateResponse(request, DicomStatus.Success);
            
            if (request.Dataset != null)
            {
                response.Dataset = request.Dataset;
            }

            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8140 },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0102 },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
            };

            SetCommandDataset(response, command);

            _session.FilmSession = new DicomFilmSession(request.Dataset)
            {
                SOPInstanceUID = request.SOPInstanceUID.UID
            };

            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 Film Session 创建失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNCreateResponse> HandleFilmBoxCreateAsync(DicomNCreateRequest request)
    {
        try
        {
            if (_session.FilmSession == null)
            {
                return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            // 更新打印作业的 FilmBoxId
            await _repository.UpdatePrintJobAsync(
                _session.FilmSession.SOPInstanceUID,
                filmBoxId: request.SOPInstanceUID.UID,
                parameters: new Dictionary<string, object>
                {
                    ["FilmOrientation"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmOrientation, "PORTRAIT") ?? "PORTRAIT",
                    ["FilmSizeID"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmSizeID, "8INX10IN") ?? "8INX10IN",
                    ["ImageDisplayFormat"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1") ?? "STANDARD\\1,1",
                    ["MagnificationType"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.MagnificationType, "REPLICATE") ?? "REPLICATE",
                    ["SmoothingType"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.SmoothingType, "MEDIUM") ?? "MEDIUM",
                    ["BorderDensity"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.BorderDensity, "BLACK") ?? "BLACK",
                    ["EmptyImageDensity"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.EmptyImageDensity, "BLACK") ?? "BLACK",
                    ["Trim"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.Trim, "NO") ?? "NO"
                });

            var responseDataset = new DicomDataset();
            
            // 复制原始请求中的属性
            if (request.Dataset != null)
            {
                foreach (var item in request.Dataset)
                {
                    if (item.Tag != DicomTag.ReferencedImageBoxSequence)
                    {
                        responseDataset.Add(item);
                    }
                }
            }

            responseDataset.AddOrUpdate(DicomTag.SOPClassUID, request.SOPClassUID);
            responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, request.SOPInstanceUID);

            var imageBoxUid = DicomUID.Generate();
            var imageBoxSequence = new DicomSequence(DicomTag.ReferencedImageBoxSequence);
            var imageBoxDataset = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, DicomUID.BasicGrayscaleImageBox },
                { DicomTag.ReferencedSOPInstanceUID, $"{request.SOPInstanceUID.UID}.1" },
                { DicomTag.ImageBoxPosition, (ushort)1 }
            };
            imageBoxSequence.Items.Add(imageBoxDataset);
            responseDataset.Add(imageBoxSequence);

            var response = new DicomNCreateResponse(request, DicomStatus.Success);
            response.Dataset = responseDataset;

            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8140 },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0102 },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
            };

            SetCommandDataset(response, command);

            _session.CurrentFilmBox = new DicomFilmBox(request.Dataset)
            {
                SOPInstanceUID = request.SOPInstanceUID.UID
            };

            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 Film Box 创建失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-SET 请求 - SOP Class: {SopClass}, Calling AE: {CallingAE}", 
                request.SOPClassUID?.Name ?? "Unknown", 
                _session.CallingAE ?? "Unknown");

            // 记录请求数据集的内容
            if (request.Dataset != null)
            {
                DicomLogger.Debug("PrintSCP", "N-SET 请求数据集:");
                foreach (var element in request.Dataset)
                {
                    DicomLogger.Debug("PrintSCP", "  Tag: {Tag}, VR: {VR}, Value: {Value}",
                        element.Tag,
                        element.ValueRepresentation,
                        element.ToString());
                }
            }

            if (_session.FilmSession == null)
            {
                DicomLogger.Warning("PrintSCP", "Film Session 为空，无法处理 N-SET 请求");
                return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            if (request.Dataset != null && request.Dataset.Contains(DicomTag.BasicGrayscaleImageSequence))
            {
                var imageSequence = request.Dataset.GetSequence(DicomTag.BasicGrayscaleImageSequence);
                if (imageSequence != null && imageSequence.Items.Count > 0)
                {
                    var firstImage = imageSequence.Items[0];
                    
                    // 保存图像
                    var dateFolder = DateTime.Now.ToString("yyyyMMdd");
                    var printFolder = Path.Combine(_printPath, dateFolder);
                    Directory.CreateDirectory(printFolder);

                    var fileName = $"{request.SOPInstanceUID.UID}.dcm";
                    var filePath = Path.Combine(printFolder, fileName);
                    var relativePath = Path.Combine("prints", dateFolder, fileName);

                    DicomLogger.Information("PrintSCP", "准备保存图像 - 路径: {FilePath}", filePath);

                    var newDataset = new DicomDataset();
                    newDataset.Add(DicomTag.SOPClassUID, DicomUID.BasicGrayscaleImageBox);
                    newDataset.Add(DicomTag.SOPInstanceUID, request.SOPInstanceUID);
                    newDataset.Add(DicomTag.Modality, "OT");
                    newDataset.Add(DicomTag.ConversionType, "WSD");
                    newDataset.Add(DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd"));
                    newDataset.Add(DicomTag.StudyTime, DateTime.Now.ToString("HHmmss"));
                    
                    // 从原始数据集中获取 StudyInstanceUID，如果没有则生成新的
                    var studyUid = firstImage.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, DicomUID.Generate().UID);
                    newDataset.Add(DicomTag.StudyInstanceUID, studyUid);
                    
                    newDataset.Add(DicomTag.SeriesInstanceUID, DicomUID.Generate());
                    newDataset.Add(DicomTag.StudyID, "1");
                    newDataset.Add(DicomTag.SeriesNumber, "1");
                    newDataset.Add(DicomTag.InstanceNumber, "1");

                    DicomLogger.Debug("PrintSCP", "图像数据集内容:");
                    foreach (var item in firstImage)
                    {
                        DicomLogger.Debug("PrintSCP", "  Tag: {Tag}, VR: {VR}, Value: {Value}",
                            item.Tag,
                            item.ValueRepresentation,
                            item.ToString());

                        if (!newDataset.Contains(item.Tag))
                        {
                            newDataset.Add(item);
                        }
                    }

                    var file = new DicomFile(newDataset);
                    await file.SaveAsync(filePath);
                    DicomLogger.Information("PrintSCP", "图像已保存 - 路径: {FilePath}", filePath);

                    // 更新打印作业
                    var parameters = new Dictionary<string, object>();
                    parameters["ImagePath"] = relativePath;
                    parameters["Status"] = PrintJobStatus.ImageReceived.ToString();

                    if (!string.IsNullOrEmpty(studyUid))
                    {
                        parameters["StudyInstanceUID"] = studyUid;
                    }

                    // 使用 FilmSession.SOPInstanceUID 更新打印作业
                    if (_session.FilmSession != null)
                    {
                        DicomLogger.Information("PrintSCP", "更新打印作业 - FilmSessionUID: {Uid}, ImagePath: {Path}, Status: {Status}",
                            _session.FilmSession.SOPInstanceUID,
                            relativePath,
                            PrintJobStatus.ImageReceived);

                        await _repository.UpdatePrintJobAsync(
                            _session.FilmSession.SOPInstanceUID,
                            parameters: parameters);
                        DicomLogger.Information("PrintSCP", "打印作业已更新");
                    }
                    else
                    {
                        DicomLogger.Warning("PrintSCP", "FilmSession 为空，无法更新打印作业");
                    }
                }
                else
                {
                    DicomLogger.Warning("PrintSCP", "未找到图像序列或序列为空");
                }
            }
            else
            {
                DicomLogger.Warning("PrintSCP", "请求数据集中没有图像序列");
            }

            // 创建响应
            var response = new DicomNSetResponse(request, DicomStatus.Success);

            // 设置命令 - 严格按照 toolkit 的格式
            var command = new DicomDataset();
            command.Add(DicomTag.AffectedSOPClassUID, request.SOPClassUID);  // 使用 AffectedSOPClassUID
            command.Add(DicomTag.CommandField, (ushort)0x8120);  // N-SET-RSP (33056)
            command.Add(DicomTag.MessageIDBeingRespondedTo, request.MessageID);
            command.Add(DicomTag.CommandDataSetType, (ushort)0x0101);  // 无数据集
            command.Add(DicomTag.Status, (ushort)DicomStatus.Success.Code);
            command.Add(DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID);  // 使用 AffectedSOPInstanceUID

            // 直接设置命令数据
            var commandProperty = typeof(DicomMessage).GetProperty("Command");
            commandProperty?.SetValue(response, command);

            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-SET 请求失败");
            return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try 
        {
            DicomLogger.Information("PrintSCP", "收到 N-ACTION 请求 - SOP Class: {SopClass}, Calling AE: {CallingAE}", 
                request.SOPClassUID?.Name ?? "Unknown", 
                _session.CallingAE ?? "Unknown");

            var response = new DicomNActionResponse(request, DicomStatus.Success);
            
            // 创建打印作业序列
            var responseDataset = new DicomDataset();
            var printJobSequence = new DicomSequence(new DicomTag(0x2120, 0x0070));  // ReferencedPrintJobSequence
            
            // 添加打印作业信息
            var jobClassDataset = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, DicomUID.PrintJob }
            };
            printJobSequence.Items.Add(jobClassDataset);

            var jobInstanceDataset = new DicomDataset
            {
                { DicomTag.ReferencedSOPInstanceUID, DicomUID.Generate() }
            };
            printJobSequence.Items.Add(jobInstanceDataset);
            
            responseDataset.Add(printJobSequence);
            response.Dataset = responseDataset;

            // 设置命令
            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8130 },  // N-ACTION-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0102 },  // 有数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID },
                { DicomTag.ActionTypeID, request.ActionTypeID }
            };

            var commandProperty = typeof(DicomMessage).GetProperty("Command");
            commandProperty?.SetValue(response, command);

            // 添加一个异步操作，比如记录日志
            await Task.Run(() => 
            {
                DicomLogger.Information("PrintSCP", "N-ACTION 请求处完成");
            });

            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-ACTION 请求失败");
            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        try
        {
            var response = new DicomNDeleteResponse(request, DicomStatus.Success);
            
            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8150 },  // N-DELETE-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
            };

            var commandProperty = typeof(DicomMessage).GetProperty("Command");
            commandProperty?.SetValue(response, command);

            // 根据不同的 SOP Class 清会话状态
            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                DicomLogger.Information("PrintSCP", "删除 Film Session: {Uid}", 
                    request.SOPInstanceUID?.UID ?? "Unknown");
                _session.FilmSession = null;
                _session.CurrentFilmBox = null;
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                DicomLogger.Information("PrintSCP", "删除 Film Box: {Uid}", 
                    request.SOPInstanceUID?.UID ?? "Unknown");
                _session.CurrentFilmBox = null;
            }

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-DELETE 请求失败");
            return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        return Task.FromResult(new DicomNGetResponse(request, DicomStatus.Success));
    }

    private void SetCommandDataset(DicomResponse response, DicomDataset command)
    {
        var commandProperty = typeof(DicomMessage).GetProperty("Command");
        commandProperty?.SetValue(response, command);
    }

    private void CleanupSession()
    {
        if (_session.CurrentFilmBox != null)
        {
            DicomLogger.Information("PrintSCP", "清理 Film Box: {Uid}", 
                _session.CurrentFilmBox.SOPInstanceUID);
        }
        if (_session.FilmSession != null)
        {
            DicomLogger.Information("PrintSCP", "清理 Film Session: {Uid}", 
                _session.FilmSession.SOPInstanceUID);
        }

        _session = new PrintSession();
    }
}

public class DicomFilmSession
{
    public DicomFilmSession(DicomDataset? dataset)
    {
    }
    public string SOPInstanceUID { get; set; } = "";
}

public class DicomFilmBox
{
    public DicomFilmBox(DicomDataset? dataset)
    {
    }
    public string SOPInstanceUID { get; set; } = "";
} 