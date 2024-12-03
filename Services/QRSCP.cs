using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;

namespace DicomSCP.Services;

public class QRSCP : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider, IDicomCMoveProvider, IDicomCGetProvider
{
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    private static readonly string[] SupportedCharacterSets = new[]
    {
        "ISO_IR 192",   // UTF-8
        "GB18030",      // 中文简体
        "ISO_IR 100"    // Latin1
    };

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxes = new[]
    {
        DicomTransferSyntax.JPEGProcess14SV1,           // JPEG Lossless
        DicomTransferSyntax.JPEG2000Lossless,          // JPEG 2000 Lossless
        DicomTransferSyntax.JPEGLSLossless,            // JPEG-LS Lossless
        DicomTransferSyntax.ExplicitVRLittleEndian,    // Explicit Little Endian
        DicomTransferSyntax.ImplicitVRLittleEndian     // Implicit Little Endian
    };

    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;

    public QRSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        try
        {
            _settings = DicomServiceProvider.GetRequiredService<IOptions<DicomSettings>>().Value;
            _repository = DicomServiceProvider.GetRequiredService<DicomRepository>();
            DicomLogger.Information("QRSCP", "QR服务初始化完成");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "QR服务初始化失败");
            throw;
        }
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("QRSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                association.CalledAE, association.CallingAE);

            if (_settings.QRSCP.ValidateCallingAE)
            {
                if (string.IsNullOrEmpty(association.CallingAE))
                {
                    DicomLogger.Warning("QRSCP", "拒绝空的 Calling AE");
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }

                if (!_settings.QRSCP.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
                {
                    DicomLogger.Warning("QRSCP", "拒绝未授权的 Calling AE: {CallingAE}", association.CallingAE);
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }
            }

            if (!string.Equals(_settings.QRSCP.AeTitle, association.CalledAE, StringComparison.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("QRSCP", "拒绝错误的 Called AE: {CalledAE}, 期望: {ExpectedAE}", 
                    association.CalledAE, _settings.QRSCP.AeTitle);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            foreach (var pc in association.PresentationContexts)
            {
                // 让 fo-dicom 处理传输语法协商
                pc.AcceptTransferSyntaxes(pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None 
                    ? AcceptedImageTransferSyntaxes 
                    : AcceptedTransferSyntaxes);
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "处理关联请求时发生错误");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("QRSCP", "接收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("QRSCP", "接收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("QRSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Information("QRSCP", "连接正常关闭");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("QRSCP", "收到 C-ECHO 请求 - 来自: {CallingAE}", Association.CallingAE);
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        DicomLogger.Information("QRSCP", "收到 C-FIND 请求 - 来自: {CallingAE}, 级别: {Level}", 
            Association.CallingAE, request.Level);

        if (request.Level != DicomQueryRetrieveLevel.Study && 
            request.Level != DicomQueryRetrieveLevel.Series && 
            request.Level != DicomQueryRetrieveLevel.Image &&
            request.Level != DicomQueryRetrieveLevel.Patient)
        {
            yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveIdentifierDoesNotMatchSOPClass);
            yield break;
        }

        // 使用 fo-dicom 的查询处理
        var responses = request.Level switch
        {
            DicomQueryRetrieveLevel.Patient => await HandlePatientLevelFind(request),
            DicomQueryRetrieveLevel.Study => await HandleStudyLevelFind(request),
            DicomQueryRetrieveLevel.Series => await HandleSeriesLevelFind(request),
            DicomQueryRetrieveLevel.Image => await HandleImageLevelFind(request),
            _ => new List<DicomCFindResponse>()
        };

        foreach (var response in responses)
        {
            yield return response;
        }

        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }

    private async Task<List<DicomCFindResponse>> HandleStudyLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // 从请求中获取查询参数
            var queryParams = ExtractStudyQueryParameters(request);
            
            // 从数据库查询数据
            var studies = await Task.Run(() => _repository.GetStudies(
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.StudyDate));

            // 构建响应
            foreach (var study in studies)
            {
                var response = CreateStudyResponse(request, study);
                responses.Add(response);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Study级失败");
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }

    private record StudyQueryParameters(
        string PatientId,
        string PatientName,
        string AccessionNumber,
        string StudyDate);

    private StudyQueryParameters ExtractStudyQueryParameters(DicomCFindRequest request)
    {
        return new StudyQueryParameters(
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty),
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty),
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty),
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty));
    }

    private DicomCFindResponse CreateStudyResponse(DicomCFindRequest request, Study study)
    {
        var response = new DicomCFindResponse(request, DicomStatus.Pending);
        var dataset = new DicomDataset();

        AddCommonTags(dataset, request.Dataset);

        dataset.Add(DicomTag.StudyInstanceUID, study.StudyInstanceUid)
              .Add(DicomTag.StudyDate, study.StudyDate ?? string.Empty)
              .Add(DicomTag.StudyTime, study.StudyTime ?? string.Empty)
              .Add(DicomTag.PatientName, study.PatientName ?? string.Empty)
              .Add(DicomTag.PatientID, study.PatientId ?? string.Empty)
              .Add(DicomTag.StudyDescription, study.StudyDescription ?? string.Empty)
              .Add(DicomTag.Modality, study.Modality ?? string.Empty)
              .Add(DicomTag.AccessionNumber, study.AccessionNumber ?? string.Empty);

        CopyRequestTags(request.Dataset, dataset);

        response.Dataset = dataset;
        return response;
    }

    private void CopyRequestTags(DicomDataset source, DicomDataset target)
    {
        foreach (var tag in source.Select(x => x.Tag))
        {
            if (!target.Contains(tag) && source.TryGetString(tag, out string value))
            {
                target.Add(tag, value);
            }
        }
    }

    private async Task<List<DicomCFindResponse>> HandleSeriesLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "Series级别查询缺少StudyInstanceUID");
            return responses;
        }

        // 使用 Task.Run 来异步执行数查询
        var seriesList = await Task.Run(() => _repository.GetSeriesByStudyUid(studyInstanceUid));

        foreach (var series in seriesList)
        {
            var response = new DicomCFindResponse(request, DicomStatus.Pending);
            var dataset = new DicomDataset();

            // 添加字符集和其他通用标签
            AddCommonTags(dataset, request.Dataset);

            // 设置必要的字段
            dataset.Add(DicomTag.StudyInstanceUID, series.StudyInstanceUid);
            dataset.Add(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid);
            dataset.Add(DicomTag.Modality, series.Modality ?? string.Empty);
            dataset.Add(DicomTag.SeriesNumber, series.SeriesNumber ?? string.Empty);
            dataset.Add(DicomTag.SeriesDescription, series.SeriesDescription ?? string.Empty);
            dataset.Add(DicomTag.NumberOfSeriesRelatedInstances, series.NumberOfInstances);

            // 复制请求的其他查询字段（如果不存在）
            foreach (var tag in request.Dataset.Select(x => x.Tag))
            {
                if (!dataset.Contains(tag) && request.Dataset.TryGetString(tag, out string value))
                {
                    dataset.Add(tag, value);
                }
            }

            response.Dataset = dataset;
            responses.Add(response);
        }

        DicomLogger.Information("QRSCP", "Series级别查询完成 - 返回记录数: {Count}", responses.Count);
        return responses;
    }

    private async Task<List<DicomCFindResponse>> HandleImageLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
        
        if (string.IsNullOrEmpty(studyInstanceUid) || string.IsNullOrEmpty(seriesInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "Image级别查询缺少StudyInstanceUID或SeriesInstanceUID");
            return responses;
        }

        // 使用 Task.Run 来异步执行数据库查询
        var instances = await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));

        foreach (var instance in instances)
        {
            try
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                // 添加字符集和其他通用标签
                AddCommonTags(dataset, request.Dataset);

                // 验证 UID 格式
                var validStudyUid = ValidateUID(studyInstanceUid);
                var validSeriesUid = ValidateUID(seriesInstanceUid);
                var validSopInstanceUid = ValidateUID(instance.SopInstanceUid);
                var validSopClassUid = ValidateUID(instance.SopClassUid);

                // 设置必要的字段
                dataset.Add(DicomTag.StudyInstanceUID, validStudyUid);
                dataset.Add(DicomTag.SeriesInstanceUID, validSeriesUid);
                dataset.Add(DicomTag.SOPInstanceUID, validSopInstanceUid);
                dataset.Add(DicomTag.SOPClassUID, validSopClassUid);
                dataset.Add(DicomTag.InstanceNumber, instance.InstanceNumber ?? string.Empty);

                response.Dataset = dataset;
                responses.Add(response);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "创建Image响应失败 - SOPInstanceUID: {SopInstanceUid}", 
                    instance.SopInstanceUid);
                continue;
            }
        }

        DicomLogger.Information("QRSCP", "Image级别查询完成 - 返回记录数: {Count}", responses.Count);
        return responses;
    }

    private string ValidateUID(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return string.Empty;
        
        try
        {
            // 分割 UID
            var parts = uid.Split('.');
            var validParts = new List<string>();

            foreach (var part in parts)
            {
                // 跳过空组件
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                // 移除前导零并确保至少保留一个数字
                var trimmed = part.TrimStart('0');
                validParts.Add(string.IsNullOrEmpty(trimmed) ? "0" : trimmed);
            }

            // 确保至少有两个组件
            if (validParts.Count < 2)
            {
                DicomLogger.Warning("QRSCP", "无效的UID格式 (组件数量不足): {Uid}", uid);
                return "0.0";  // 返回一个有效的默认UID
            }

            return string.Join(".", validParts);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "UID验证失败: {Uid}", uid);
            return "0.0";  // 返回一个有效的默认UID
        }
    }

    private string GetPreferredCharacterSet(DicomDataset requestDataset)
    {
        // 获取请中的字符集
        var requestedCharacterSet = requestDataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");

        // 如果请求的字符集持列表中，使用请求的字符集
        if (SupportedCharacterSets.Contains(requestedCharacterSet))
        {
            return requestedCharacterSet;
        }

        // 否则默认使用 UTF-8
        return "ISO_IR 192";
    }

    private void AddCommonTags(DicomDataset dataset, DicomDataset requestDataset)
    {
        // 让 fo-dicom 处理字符集
        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, 
            requestDataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192"));
    }

    public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
    {
        DicomLogger.Information("QRSCP", "收到 C-MOVE 请求 - 来自: {CallingAE}, 目标: {DestinationAE}, 级别: {Level}", 
            Association.CallingAE, request.DestinationAE, request.Level);

        // 验证目标 AE 是否在配置的 MoveDestinations 中
        var destinationConfig = _settings.QRSCP.MoveDestinations
            .FirstOrDefault(ae => ae.AeTitle.Equals(request.DestinationAE, StringComparison.OrdinalIgnoreCase));

        if (destinationConfig == null)
        {
            DicomLogger.Warning("QRSCP", "未找到目标AE配置: {DestinationAE}", request.DestinationAE);
            yield return new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown);
            yield break;
        }

        // 获取实例列表
        var instances = await GetRequestedInstances(request);
        if (!instances.Any())
        {
            yield return new DicomCMoveResponse(request, DicomStatus.Success);
            yield break;
        }

        DicomLogger.Information("QRSCP", "开始 C-MOVE 操作 - 总实例数: {Total}", instances.Count());

        // 使用目标配置创建 DICOM 客户端
        var client = CreateDicomClient(destinationConfig);
        var failedCount = 0;
        var successCount = 0;

        // 先测试目标连接
        DicomCMoveResponse? connectionErrorResponse = null;
        try
        {
            await client.AddRequestAsync(new DicomCEchoRequest());
            await client.SendAsync();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "目标节点连接失败 - AE: {AE}, Host: {Host}, Port: {Port}", 
                destinationConfig.AeTitle, destinationConfig.HostName, destinationConfig.Port);
            connectionErrorResponse = new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown)
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.NumberOfRemainingSuboperations, 0 },
                    { DicomTag.NumberOfCompletedSuboperations, 0 },
                    { DicomTag.NumberOfFailedSuboperations, instances.Count() }
                }
            };
        }

        if (connectionErrorResponse != null)
        {
            yield return connectionErrorResponse;
            yield break;
        }

        // 发送每个实例
        foreach (var instance in instances)
        {
            DicomCMoveResponse? errorResponse = null;
            try
            {
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!File.Exists(filePath))
                {
                    DicomLogger.Warning("QRSCP", "未找到文件: {FilePath}", filePath);
                    failedCount++;
                    continue;
                }

                var file = DicomFile.Open(filePath);
                var storeRequest = new DicomCStoreRequest(file);
                await client.AddRequestAsync(storeRequest);
                await client.SendAsync();
                successCount++;
                
                DicomLogger.Information("QRSCP", 
                    "实例发送成功 - SOPInstanceUID: {SopInstanceUid}, 进度: {Success}/{Total}", 
                    instance.SopInstanceUid, successCount, instances.Count());
            }
            catch (Exception ex)
            {
                if (IsNetworkError(ex))
                {
                    DicomLogger.Error("QRSCP", ex, "网络错误，中止传输 - SOPInstanceUID: {SopInstanceUid}", 
                        instance.SopInstanceUid);
                    
                    errorResponse = new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown)
                    {
                        Dataset = new DicomDataset
                        {
                            { DicomTag.NumberOfRemainingSuboperations, instances.Count() - (successCount + 1) },
                            { DicomTag.NumberOfCompletedSuboperations, successCount },
                            { DicomTag.NumberOfFailedSuboperations, instances.Count() - successCount }
                        }
                    };
                }
                else
                {
                    DicomLogger.Error("QRSCP", ex, "实例处理失败 - SOPInstanceUID: {SopInstanceUid}", 
                        instance.SopInstanceUid);
                    failedCount++;
                    continue;
                }
            }

            if (errorResponse != null)
            {
                yield return errorResponse;
                yield break;
            }

            var pendingResponse = new DicomCMoveResponse(request, DicomStatus.Pending);
            pendingResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfRemainingSuboperations, 
                instances.Count() - (successCount + failedCount));
            pendingResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfCompletedSuboperations, successCount);
            yield return pendingResponse;
        }

        var finalStatus = failedCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success;
        var finalResponse = new DicomCMoveResponse(request, finalStatus);
        finalResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfRemainingSuboperations, 0);
        finalResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfCompletedSuboperations, successCount);
        if (failedCount > 0)
        {
            finalResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfFailedSuboperations, failedCount);
        }
        yield return finalResponse;
    }

    private IDicomClient CreateDicomClient(MoveDestination destination)
    {
        var client = DicomClientFactory.Create(
            destination.HostName,  // 使用配置的主机名
            destination.Port,      // 使用配置的端口
            false,
            _settings.QRSCP.AeTitle,
            destination.AeTitle);  // 使用配置的目标 AE Title

        client.NegotiateAsyncOps();
        return client;
    }

    public async IAsyncEnumerable<DicomCGetResponse> OnCGetRequestAsync(DicomCGetRequest request)
    {
        DicomLogger.Information("QRSCP", "收到 C-GET 请求 - 来自: {CallingAE}, 级别: {Level}", 
            Association.CallingAE, request.Level);

        // 获取实例列表
        var instances = await GetRequestedInstances(request);
        if (!instances.Any())
        {
            yield return new DicomCGetResponse(request, DicomStatus.Success);
            yield break;
        }

        DicomLogger.Information("QRSCP", "开始 C-GET 操作 - 总实例数: {Total}", instances.Count());

        var failedCount = 0;
        var successCount = 0;

        // 发送每个实例
        foreach (var instance in instances)
        {
            DicomCGetResponse? errorResponse = null;
            try
            {
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!File.Exists(filePath))
                {
                    DicomLogger.Warning("QRSCP", "未找到文件: {FilePath}", filePath);
                    failedCount++;
                    continue;
                }

                var file = DicomFile.Open(filePath);
                await SendRequestAsync(new DicomCStoreRequest(file));
                successCount++;

                DicomLogger.Information("QRSCP", 
                    "实例发送成功 - SOPInstanceUID: {SopInstanceUid}, 进度: {Success}/{Total}", 
                    instance.SopInstanceUid, successCount, instances.Count());
            }
            catch (Exception ex)
            {
                if (IsNetworkError(ex))
                {
                    DicomLogger.Error("QRSCP", ex, "网络错误，中止传输 - SOPInstanceUID: {SopInstanceUid}", 
                        instance.SopInstanceUid);
                    
                    errorResponse = new DicomCGetResponse(request, DicomStatus.ProcessingFailure)
                    {
                        Dataset = new DicomDataset
                        {
                            { DicomTag.NumberOfRemainingSuboperations, instances.Count() - (successCount + 1) },
                            { DicomTag.NumberOfCompletedSuboperations, successCount },
                            { DicomTag.NumberOfFailedSuboperations, instances.Count() - successCount }
                        }
                    };
                }
                else
                {
                    DicomLogger.Error("QRSCP", ex, "实例处理失败 - SOPInstanceUID: {SopInstanceUid}", 
                        instance.SopInstanceUid);
                    failedCount++;
                    continue;
                }
            }

            if (errorResponse != null)
            {
                yield return errorResponse;
                yield break;
            }

            var pendingResponse = new DicomCGetResponse(request, DicomStatus.Pending);
            pendingResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfRemainingSuboperations, 
                instances.Count() - (successCount + failedCount));
            pendingResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfCompletedSuboperations, successCount);
            yield return pendingResponse;
        }

        var finalStatus = failedCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success;
        var finalResponse = new DicomCGetResponse(request, finalStatus);
        finalResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfRemainingSuboperations, 0);
        finalResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfCompletedSuboperations, successCount);
        if (failedCount > 0)
        {
            finalResponse.Dataset?.AddOrUpdate(DicomTag.NumberOfFailedSuboperations, failedCount);
        }
        yield return finalResponse;
    }

    private async Task<IEnumerable<Instance>> GetRequestedInstances(DicomRequest request)
    {
        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
        var sopInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, string.Empty);

        try
        {
            if (!string.IsNullOrEmpty(sopInstanceUid))
            {
                var instance = await Task.Run(() => _repository.GetInstanceAsync(sopInstanceUid));
                return instance != null ? new[] { instance } : Enumerable.Empty<Instance>();
            }
            
            if (!string.IsNullOrEmpty(seriesInstanceUid))
            {
                return await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
            }
            
            if (!string.IsNullOrEmpty(studyInstanceUid))
            {
                return await Task.Run(() => _repository.GetInstancesByStudyUid(studyInstanceUid));
            }

            DicomLogger.Warning("QRSCP", "请求缺少必要的UID");
            return Enumerable.Empty<Instance>();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "获取实例列表失败");
            return Enumerable.Empty<Instance>();
        }
    }

    private bool IsNetworkError(Exception ex)
    {
        // 检查是否是网络相关的异常
        return ex is System.Net.Sockets.SocketException ||
               ex is System.Net.WebException ||
               ex is System.IO.IOException ||
               ex is DicomNetworkException ||
               (ex.InnerException != null && IsNetworkError(ex.InnerException));
    }

    private async Task<List<DicomCFindResponse>> HandlePatientLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // 从请求中获取查询参数
            var patientId = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty);
            var patientName = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty);

            // 从数据库查询患者数据
            var patients = await Task.Run(() => _repository.GetPatients(patientId, patientName));

            // 构建响应
            foreach (var patient in patients)
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                AddCommonTags(dataset, request.Dataset);

                dataset.Add(DicomTag.PatientID, patient.PatientId ?? string.Empty)
                      .Add(DicomTag.PatientName, patient.PatientName ?? string.Empty)
                      .Add(DicomTag.PatientBirthDate, patient.PatientBirthDate ?? string.Empty)
                      .Add(DicomTag.PatientSex, patient.PatientSex ?? string.Empty);

                response.Dataset = dataset;
                responses.Add(response);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Patient级查询失败");
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }
}