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
    private readonly string _printPath;
    private readonly string _relativePrintPath = "prints";
    private readonly DicomSettings _settings;

    // 会话状态
    private DicomFilmSession? _filmSession;
    private DicomFilmBox? _currentFilmBox;
    private string? _callingAE;

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
        _printPath = Path.Combine(_settings.StoragePath, _relativePrintPath);
        Directory.CreateDirectory(_printPath);
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        DicomLogger.Information("PrintSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            association.CalledAE, 
            association.CallingAE);

        if (_settings?.PrintSCP.AeTitle != association.CalledAE)
        {
            DicomLogger.Warning("PrintSCP", "拒绝错误的 Called AE: {CalledAE}, 期望: {ExpectedAE}", 
                association.CalledAE, 
                _settings?.PrintSCP.AeTitle ?? "未配置");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.CalledAENotRecognized);
        }

        _callingAE = association.CallingAE;

        foreach (var pc in association.PresentationContexts)
        {
            DicomLogger.Information("PrintSCP", "处理表示上下文 - Abstract Syntax: {AbstractSyntax}", 
                pc.AbstractSyntax.Name);
            pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
        }

        DicomLogger.Information("PrintSCP", "接受关联请求");
        return SendAssociationAcceptAsync(association);
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

            // 如果客户端没有正确清理资源，在连接关闭时记录并清理
            if (_currentFilmBox != null)
            {
                DicomLogger.Information("PrintSCP", "连接关闭时清理 Film Box: {Uid}", 
                    _currentFilmBox.SOPInstanceUID ?? "Unknown");
            }
            if (_filmSession != null)
            {
                DicomLogger.Information("PrintSCP", "连接关闭时清理 Film Session: {Uid}", 
                    _filmSession.SOPInstanceUID ?? "Unknown");
            }

            _filmSession = null;
            _currentFilmBox = null;
            _callingAE = null;
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
                _callingAE ?? "Unknown");

            // 添加一些实际的异步操作
            await Task.Delay(1);  // 添加一个小延迟以确保异步性质

            var response = new DicomNCreateResponse(request, DicomStatus.Success);

            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                if (request.Dataset != null)
                {
                    response.Dataset = request.Dataset;
                }

                var command = new DicomDataset
                {
                    { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                    { DicomTag.CommandField, (ushort)0x8140 },  // N-CREATE-RSP
                    { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                    { DicomTag.CommandDataSetType, (ushort)0x0102 },  // 有数据集
                    { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                    { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
                };

                var commandProperty = typeof(DicomMessage).GetProperty("Command");
                commandProperty?.SetValue(response, command);

                _filmSession = new DicomFilmSession(request.Dataset)
                {
                    SOPInstanceUID = request.SOPInstanceUID.UID
                };
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                if (_filmSession == null)
                {
                    return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
                }

                var responseDataset = new DicomDataset();
                
                // 复制原始请求中的属性
                if (request.Dataset != null)
                {
                    foreach (var item in request.Dataset)
                    {
                        // 跳过 ReferencedImageBoxSequence，我们会重新创建它
                        if (item.Tag != DicomTag.ReferencedImageBoxSequence)
                        {
                            responseDataset.Add(item);
                        }
                    }
                }

                // 添加必要的属性
                responseDataset.AddOrUpdate(DicomTag.SOPClassUID, request.SOPClassUID);
                responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, request.SOPInstanceUID);

                // 创建新的图像盒序列
                var imageBoxUid = DicomUID.Generate();
                var imageBoxSequence = new DicomSequence(DicomTag.ReferencedImageBoxSequence);
                var imageBoxDataset = new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, DicomUID.BasicGrayscaleImageBox },
                    { DicomTag.ReferencedSOPInstanceUID, $"{request.SOPInstanceUID.UID}.1" },  // 使用 .1 后缀
                    { DicomTag.ImageBoxPosition, (ushort)1 }
                };
                imageBoxSequence.Items.Add(imageBoxDataset);
                responseDataset.Add(imageBoxSequence);

                response.Dataset = responseDataset;

                var command = new DicomDataset
                {
                    { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                    { DicomTag.CommandField, (ushort)0x8140 },  // N-CREATE-RSP
                    { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                    { DicomTag.CommandDataSetType, (ushort)0x0102 },  // 有数据集
                    { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                    { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
                };

                var commandProperty = typeof(DicomMessage).GetProperty("Command");
                commandProperty?.SetValue(response, command);

                _currentFilmBox = new DicomFilmBox(request.Dataset)
                {
                    SOPInstanceUID = request.SOPInstanceUID.UID
                };
            }

            return response;
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
            DicomLogger.Information("PrintSCP", "收到 N-SET 请求 - SOP Class: {SopClass}, Calling AE: {CallingAE}", 
                request.SOPClassUID?.Name ?? "Unknown", 
                _callingAE ?? "Unknown");

            if (_filmSession == null)
            {
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

                    var newDataset = new DicomDataset();
                    newDataset.Add(DicomTag.SOPClassUID, DicomUID.BasicGrayscaleImageBox);
                    newDataset.Add(DicomTag.SOPInstanceUID, request.SOPInstanceUID);
                    newDataset.Add(DicomTag.Modality, "OT");
                    newDataset.Add(DicomTag.ConversionType, "WSD");
                    newDataset.Add(DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd"));
                    newDataset.Add(DicomTag.StudyTime, DateTime.Now.ToString("HHmmss"));
                    newDataset.Add(DicomTag.StudyInstanceUID, DicomUID.Generate());
                    newDataset.Add(DicomTag.SeriesInstanceUID, DicomUID.Generate());
                    newDataset.Add(DicomTag.StudyID, "1");
                    newDataset.Add(DicomTag.SeriesNumber, "1");
                    newDataset.Add(DicomTag.InstanceNumber, "1");

                    foreach (var item in firstImage)
                    {
                        if (!newDataset.Contains(item.Tag))
                        {
                            newDataset.Add(item);
                        }
                    }

                    var file = new DicomFile(newDataset);
                    await file.SaveAsync(filePath);
                }
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

            // 直接设置命令数据���
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

    public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try 
        {
            DicomLogger.Information("PrintSCP", "收到 N-ACTION 请求 - SOP Class: {SopClass}, Calling AE: {CallingAE}", 
                request.SOPClassUID?.Name ?? "Unknown", 
                _callingAE ?? "Unknown");

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

            DicomLogger.Information("PrintSCP", "N-ACTION 请求处理完成");
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-ACTION 请求失败");
            return Task.FromResult(new DicomNActionResponse(request, DicomStatus.ProcessingFailure));
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

            // 根据不同的 SOP Class 清理会话状态
            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                DicomLogger.Information("PrintSCP", "删除 Film Session: {Uid}", 
                    request.SOPInstanceUID?.UID ?? "Unknown");
                _filmSession = null;
                _currentFilmBox = null;
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                DicomLogger.Information("PrintSCP", "删除 Film Box: {Uid}", 
                    request.SOPInstanceUID?.UID ?? "Unknown");
                _currentFilmBox = null;
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