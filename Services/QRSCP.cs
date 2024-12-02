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
            DicomLogger.Information("QRSCP", "收到关联请求 - AET: {CallingAE}", association.CallingAE);

            if (_settings.QRSCP.ValidateCallingAE && 
                !_settings.QRSCP.AllowedCallingAETitles.Contains(association.CallingAE))
            {
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
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
            request.Level != DicomQueryRetrieveLevel.Image)
        {
            yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveIdentifierDoesNotMatchSOPClass);
            yield break;
        }

        // 使用 fo-dicom 的查询处理
        var responses = request.Level switch
        {
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
            DicomLogger.Error("QRSCP", ex, "Study级别查询失败");
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

            // 复制请求中的其他查询字段（如果不存在）
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
            var response = new DicomCFindResponse(request, DicomStatus.Pending);
            var dataset = new DicomDataset();

            // 添加字符集和其他通用标签
            AddCommonTags(dataset, request.Dataset);

            // 设置必要的字段
            dataset.Add(DicomTag.StudyInstanceUID, studyInstanceUid);
            dataset.Add(DicomTag.SeriesInstanceUID, seriesInstanceUid);
            dataset.Add(DicomTag.SOPInstanceUID, instance.SopInstanceUid);
            dataset.Add(DicomTag.SOPClassUID, instance.SopClassUid);
            dataset.Add(DicomTag.InstanceNumber, instance.InstanceNumber ?? string.Empty);
            
            // 复制请求中的其他查询字段（如果不存在）
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

        DicomLogger.Information("QRSCP", "Image级别查询完成 - 返回记录数: {Count}", responses.Count);
        return responses;
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

        // 验证目标 AE
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

        // 创建 DICOM 客户端
        var client = CreateDicomClient(destinationConfig);

        // 发送每个实例
        foreach (var instance in instances)
        {
            try
            {
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!File.Exists(filePath))
                {
                    DicomLogger.Warning("QRSCP", "未找到文件: {FilePath}", filePath);
                    continue;
                }

                var file = DicomFile.Open(filePath);
                var storeRequest = new DicomCStoreRequest(file);
                await client.AddRequestAsync(storeRequest);
                await client.SendAsync();
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "发送实例失败 - SOPInstanceUID: {SopInstanceUid}", instance.SopInstanceUid);
            }

            yield return new DicomCMoveResponse(request, DicomStatus.Pending);
        }

        yield return new DicomCMoveResponse(request, DicomStatus.Success);
    }

    private IDicomClient CreateDicomClient(MoveDestination destination)
    {
        var client = DicomClientFactory.Create(
            destination.HostName,
            destination.Port,
            false,
            _settings.QRSCP.AeTitle,
            destination.AeTitle);

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

        // 发送每个实例，让基类处理计数和状态
        foreach (var instance in instances)
        {
            try
            {
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!File.Exists(filePath))
                {
                    DicomLogger.Warning("QRSCP", "未找到文件: {FilePath}", filePath);
                    continue;
                }

                var file = DicomFile.Open(filePath);
                await SendRequestAsync(new DicomCStoreRequest(file));
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "发送实例失败 - SOPInstanceUID: {SopInstanceUid}", instance.SopInstanceUid);
            }

            yield return new DicomCGetResponse(request, DicomStatus.Pending);
        }

        yield return new DicomCGetResponse(request, DicomStatus.Success);
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
}