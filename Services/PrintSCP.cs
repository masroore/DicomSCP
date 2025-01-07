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
            // 生成 FilmSessionId，不依赖 SOPInstanceUID
            var filmSessionId = DicomUID.Generate().UID;
            var printJob = new PrintJob
            {
                JobId = $"{DateTime.Now:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}",
                FilmSessionId = filmSessionId,  // 使用生成的ID
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
                var responseDataset = new DicomDataset();
                foreach (var item in request.Dataset)
                {
                    responseDataset.Add(item);
                }
                responseDataset.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.BasicFilmSession);
                responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, filmSessionId);
                response.Dataset = responseDataset;
            }

            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, DicomUID.BasicFilmSession },
                { DicomTag.CommandField, (ushort)0x8140 },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0102 },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, filmSessionId }
            };

            SetCommandDataset(response, command);

            _session.FilmSession = new DicomFilmSession(request.Dataset)
            {
                SOPInstanceUID = filmSessionId
            };

            DicomLogger.Information("PrintSCP", "Film Session创建成功 - ID: {Id}", filmSessionId);
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

            // 生成 FilmBoxId
            var filmBoxId = DicomUID.Generate().UID;

            await _repository.UpdatePrintJobAsync(
                _session.FilmSession.SOPInstanceUID,
                filmBoxId: filmBoxId,
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
            responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, filmBoxId);

            // 使用filmBoxId创建Image Box引用
            var imageBoxSequence = new DicomSequence(DicomTag.ReferencedImageBoxSequence);
            var imageBoxDataset = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, DicomUID.BasicGrayscaleImageBox },
                { DicomTag.ReferencedSOPInstanceUID, $"{filmBoxId}.1" },
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
                { DicomTag.AffectedSOPInstanceUID, filmBoxId }  // 使用生成的filmBoxId
            };

            SetCommandDataset(response, command);

            _session.CurrentFilmBox = new DicomFilmBox(request.Dataset)
            {
                SOPInstanceUID = filmBoxId
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

            if (_session.FilmSession == null || _session.CurrentFilmBox == null)
            {
                DicomLogger.Warning("PrintSCP", "Film Session或Film Box为空，无法处理 N-SET 请求");
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

                    // 使用Film Box的ID生成Image Box ID
                    var imageBoxId = $"{_session.CurrentFilmBox.SOPInstanceUID}.1";
                    var fileName = $"{imageBoxId}.dcm";
                    var filePath = Path.Combine(printFolder, fileName);
                    var relativePath = Path.Combine("prints", dateFolder, fileName);

                    DicomLogger.Information("PrintSCP", "准备保存图像 - 路径: {FilePath}", filePath);

                    // 直接克隆原始数据集，保留所有数据（包括像素数据）
                    var newDataset = firstImage.Clone();
                    
                    // 获取或生成StudyInstanceUID
                    var studyInstanceUid = firstImage.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, _session.FilmSession.SOPInstanceUID);
                    
                    // 更新必要的标签
                    newDataset.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.BasicGrayscaleImageBox);
                    newDataset.AddOrUpdate(DicomTag.SOPInstanceUID, imageBoxId);
                    newDataset.AddOrUpdate(DicomTag.Modality, "OT");
                    newDataset.AddOrUpdate(DicomTag.ConversionType, "WSD");
                    newDataset.AddOrUpdate(DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd"));
                    newDataset.AddOrUpdate(DicomTag.StudyTime, DateTime.Now.ToString("HHmmss"));
                    newDataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyInstanceUid);
                    newDataset.AddOrUpdate(DicomTag.SeriesInstanceUID, imageBoxId);
                    newDataset.AddOrUpdate(DicomTag.StudyID, "1");
                    newDataset.AddOrUpdate(DicomTag.SeriesNumber, "1");
                    newDataset.AddOrUpdate(DicomTag.InstanceNumber, "1");

                    var file = new DicomFile(newDataset);
                    await file.SaveAsync(filePath);
                    DicomLogger.Information("PrintSCP", "图像已保存 - 路径: {FilePath}", filePath);

                    // 更新打印作业
                    await _repository.UpdatePrintJobAsync(
                        _session.FilmSession.SOPInstanceUID,
                        parameters: new Dictionary<string, object>
                        {
                            ["ImagePath"] = relativePath,
                            ["Status"] = PrintJobStatus.ImageReceived.ToString(),
                            ["StudyInstanceUID"] = studyInstanceUid
                        });
                }
            }

            // 创建响应
            var response = new DicomNSetResponse(request, DicomStatus.Success);

            // 设置命令
            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8120 },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, $"{_session.CurrentFilmBox.SOPInstanceUID}.1" }  // 使用Image Box ID
            };

            SetCommandDataset(response, command);
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

            if (_session.FilmSession == null)
            {
                DicomLogger.Warning("PrintSCP", "Film Session为空，无法处理 N-ACTION 请求");
                return new DicomNActionResponse(request, DicomStatus.NoSuchObjectInstance);
            }

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
                { DicomTag.AffectedSOPInstanceUID, _session.FilmSession.SOPInstanceUID },
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