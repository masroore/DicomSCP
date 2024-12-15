using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Imaging.Codec;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;

namespace DicomSCP.Services;

public class QRSCP : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider, IDicomCMoveProvider, IDicomCGetProvider, IDicomCStoreProvider
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
        DicomTransferSyntax.JPEGLSLossless,            // JPEG-LS Lossless
        DicomTransferSyntax.JPEG2000Lossless,          // JPEG 2000 Lossless
        DicomTransferSyntax.JPEGProcess14SV1,          // JPEG Lossless
        DicomTransferSyntax.RLELossless,               // RLE Lossless
        DicomTransferSyntax.JPEGLSNearLossless,        // JPEG-LS Near Lossless
        DicomTransferSyntax.JPEG2000Lossy,             // JPEG 2000 Lossy
        DicomTransferSyntax.ExplicitVRLittleEndian,    // Explicit Little Endian
        DicomTransferSyntax.ImplicitVRLittleEndian,    // Implicit Little Endian
        DicomTransferSyntax.ExplicitVRBigEndian        // Explicit Big Endian
    };

    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;
    private bool _associationReleaseLogged = false;
    private bool _transferSyntaxLogged = false;

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
            DicomLogger.Error("QRSCP", ex, "QR服务初始化失");
            throw;
        }
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("QRSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                association.CalledAE, association.CallingAE);

            // 验证 Called AE
            var calledAE = association.CalledAE;
            var expectedAE = _settings?.QRSCP.AeTitle ?? string.Empty;

            if (!string.Equals(expectedAE, calledAE, StringComparison.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("QRSCP", "拒绝错误的 Called AE: {CalledAE}, 期望: {ExpectedAE}", 
                    calledAE, expectedAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            // 验证 Calling AE
            if (string.IsNullOrEmpty(association.CallingAE))
            {
                DicomLogger.Warning("QRSCP", "拒绝空的 Calling AE");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }

            // 只在配置了验证时才检查 AllowedCallingAEs
            if (_settings?.QRSCP.ValidateCallingAE == true)
            {
                if (!_settings.QRSCP.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
                {
                    DicomLogger.Warning("QRSCP", "拒绝未授权的调用方AE: {CallingAE}", association.CallingAE);
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }
            }

            DicomLogger.Information("QRSCP", "验证通过 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                calledAE, association.CallingAE);

            var storageCount = 0;
            foreach (var pc in association.PresentationContexts)
            {
                // 检查是否支持请求的服务
                if (pc.AbstractSyntax == DicomUID.Verification ||                                // C-ECHO
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelFind || // C-FIND
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelFind || // C-FIND (Patient Root)
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove || // C-MOVE
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove || // C-MOVE (Patient Root)
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet ||  // C-GET
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet || // C-GET (Patient Root)
                    pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)             // Storage (for C-GET)
                {
                    // 记录服务接受日志
                    if (pc.AbstractSyntax.StorageCategory == DicomStorageCategory.None)
                    {
                        DicomLogger.Information("QRSCP", "接受服务 - AET: {CallingAE}, 服务: {Service}", 
                            association.CallingAE, pc.AbstractSyntax.Name);
                    }

                    // 根据服务类型选择合适的传输语法
                    if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                    {
                        // 对于存储类服务（C-GET需要），接受所有支持的传输语法
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
                    }
                    else if (pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet ||
                             pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove)
                    {
                        // 对于 C-GET/C-MOVE 服务，同时接受基本传输语法和图像传输语法
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes.Concat(AcceptedTransferSyntaxes).Distinct().ToArray());
                        DicomLogger.Information("QRSCP", "为C-GET/C-MOVE服务接受传输语法 - 服务: {Service}", pc.AbstractSyntax.Name);
                    }
                    else
                    {
                        // 对于其他服务，使用基本传输语法
                        pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                        DicomLogger.Information("QRSCP", "为基本服务接受传输语法 - 服务: {Service}", pc.AbstractSyntax.Name);
                    }
                }
                else
                {
                    DicomLogger.Warning("QRSCP", "拒绝不支持的服务 - AET: {CallingAE}, 服务: {Service}", 
                        association.CallingAE, pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            // 如果有存储类服务，只记录一条汇总日志
            if (storageCount > 0)
            {
                DicomLogger.Information("QRSCP", "接受存储类服务 - AET: {CallingAE}, 支持的存储类数: {Count}", 
                    association.CallingAE, storageCount);
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "处理关联请求失败");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        if (!_associationReleaseLogged)
        {
            DicomLogger.Information("QRSCP", "接收到关联释放请求");
            _associationReleaseLogged = true;
        }
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
        _associationReleaseLogged = false;
        _transferSyntaxLogged = false;
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

        // 使 fo-dicom 的查询理
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
            
            DicomLogger.Information("QRSCP", "Study级查询参数 - PatientId: {PatientId}, PatientName: {PatientName}, " +
                "AccessionNumber: {AccessionNumber}, 日期范围: {StartDate} - {EndDate}, Modality: {Modality}",
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.DateRange.StartDate,
                queryParams.DateRange.EndDate,
                queryParams.Modalities);

            // 从数据库查询数据
            var studies = await Task.Run(() => _repository.GetStudies(
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.DateRange,
                queryParams.Modalities));

            DicomLogger.Information("QRSCP", "Study级查询结果 - 记录数: {Count}", studies.Count);

            // 构建响应
            foreach (var study in studies)
            {
                var response = CreateStudyResponse(request, study);
                responses.Add(response);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Study级查询失败: {Message}", ex.Message);
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }

    private record StudyQueryParameters(
        string PatientId,
        string PatientName,
        string AccessionNumber,
        (string StartDate, string EndDate) DateRange,
        string[] Modalities);

    private StudyQueryParameters ExtractStudyQueryParameters(DicomCFindRequest request)
    {
        // 处理可能为 null 的字符串
        string ProcessValue(string? value) => 
            (value?.Replace("*", "")) ?? string.Empty;

        // 记录原始日期值
        var studyDate = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty);
        var studyTime = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyTime, string.Empty);
        if (!string.IsNullOrEmpty(studyDate))
        {
            DicomLogger.Debug("QRSCP", "查询日期: {Date}", studyDate);
        }

        // 处理日期范围
        (string StartDate, string EndDate) ProcessDateRange(string? dateValue)
        {
            if (string.IsNullOrEmpty(dateValue))
                return (string.Empty, string.Empty);  // 返回空，查询所有记录

            // 移除可能的 VR 和标签信息
            var cleanDateValue = dateValue;
            if (dateValue.Contains("DA"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(dateValue, @"\d{8}");
                if (match.Success)
                {
                    cleanDateValue = match.Value;
                }
            }

            // 处理 DICOM 日期范围格式
            if (cleanDateValue.Contains("-"))
            {
                var parts = cleanDateValue.Split('-');
                if (parts.Length == 2)
                {
                    var startDate = parts[0].Trim();
                    var endDate = parts[1].Trim();

                    // 处理开放式范围
                    if (string.IsNullOrEmpty(startDate))
                    {
                        startDate = "19000101";  // 使用最小日期
                    }
                    if (string.IsNullOrEmpty(endDate))
                    {
                        endDate = "99991231";    // 使用最大日期
                    }

                    return (startDate, endDate);
                }
            }
            
            // 如果是个日期，开始和结束日期相同
            return (cleanDateValue, cleanDateValue);
        }

        var dateRange = ProcessDateRange(studyDate);
        DicomLogger.Debug("QRSCP", "日范围处理: 原始值={Original}, 开始={Start}, 结束={End}", 
            studyDate,
            dateRange.StartDate,
            dateRange.EndDate);

        // 处理 Modality 列表
        string[] ProcessModalities(DicomDataset dataset)
        {
            var modalities = new List<string>();

            // 尝试取 ModalitiesInStudy
            if (dataset.Contains(DicomTag.ModalitiesInStudy))
            {
                try
                {
                    var modalityValues = dataset.GetValues<string>(DicomTag.ModalitiesInStudy);
                    if (modalityValues != null && modalityValues.Length > 0)
                    {
                        modalities.AddRange(modalityValues.Where(m => !string.IsNullOrEmpty(m)));
                    }
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("QRSCP", ex, "获取 ModalitiesInStudy 失败");
                }
            }

            // 尝试获取单 Modality
            var singleModality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
            if (!string.IsNullOrEmpty(singleModality) && !modalities.Contains(singleModality))
            {
                modalities.Add(singleModality);
            }

            DicomLogger.Debug("QRSCP", "处理设备类型: {@Modalities}", modalities);
            return modalities.ToArray();
        }

        var parameters = new StudyQueryParameters(
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty)),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty)),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty)),
            dateRange,
            ProcessModalities(request.Dataset));

        DicomLogger.Debug("QRSCP", "查询参数 - PatientId: {PatientId}, PatientName: {PatientName}, " +
            "AccessionNumber: {AccessionNumber}, 日期范围: {StartDate} - {EndDate}, 设备类: {Modalities}",
            parameters.PatientId,
            parameters.PatientName,
            parameters.AccessionNumber,
            parameters.DateRange.StartDate,
            parameters.DateRange.EndDate,
            string.Join(",", parameters.Modalities));

        return parameters;
    }

    private DicomCFindResponse CreateStudyResponse(DicomCFindRequest request, Study study)
    {
        var response = new DicomCFindResponse(request, DicomStatus.Pending);
        var dataset = new DicomDataset();

        // 获取请求中的字符集，如果没有指定则默认使用 UTF-8
        var requestedCharacterSet = request.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");
        DicomLogger.Debug("QRSCP", "请求的字符集: {CharacterSet}", requestedCharacterSet);
        
        // 根据请求的字符集设置响应的字符集
        switch (requestedCharacterSet.ToUpperInvariant())
        {
            case "ISO_IR 100":  // Latin1
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                break;
            case "GB18030":     // 中文简体
                dataset.Add(DicomTag.SpecificCharacterSet, "GB18030");
                break;
            case "ISO_IR 192":  // UTF-8
            default:
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
                break;
        }

        AddCommonTags(dataset, request.Dataset);

        dataset.Add(DicomTag.StudyInstanceUID, study.StudyInstanceUid)
              .Add(DicomTag.StudyDate, study.StudyDate ?? string.Empty)
              .Add(DicomTag.StudyTime, study.StudyTime ?? string.Empty)
              .Add(DicomTag.PatientName, study.PatientName ?? string.Empty)
              .Add(DicomTag.PatientID, study.PatientId ?? string.Empty)
              .Add(DicomTag.PatientBirthDate, study.PatientBirthDate ?? string.Empty)
              .Add(DicomTag.StudyDescription, study.StudyDescription ?? string.Empty)
              .Add(DicomTag.ModalitiesInStudy, study.Modality ?? string.Empty)
              .Add(DicomTag.AccessionNumber, study.AccessionNumber ?? string.Empty)
              .Add(DicomTag.NumberOfStudyRelatedSeries, study.NumberOfStudyRelatedSeries.ToString())
              .Add(DicomTag.NumberOfStudyRelatedInstances, study.NumberOfStudyRelatedInstances.ToString());

        response.Dataset = dataset;
        return response;
    }

    private DicomCFindResponse CreateSeriesResponse(DicomCFindRequest request, Series series)
    {
        var response = new DicomCFindResponse(request, DicomStatus.Pending);
        var dataset = new DicomDataset();

        // 获取请求中的字符集，如果没有指定则默认使用 UTF-8
        var requestedCharacterSet = request.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");
        DicomLogger.Debug("QRSCP", "请求的字符集: {CharacterSet}", requestedCharacterSet);
        
        // 根据请求的字符集设置响应的字符集
        switch (requestedCharacterSet.ToUpperInvariant())
        {
            case "ISO_IR 100":  // Latin1
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                break;
            case "GB18030":     // 中文简体
                dataset.Add(DicomTag.SpecificCharacterSet, "GB18030");
                break;
            case "ISO_IR 192":  // UTF-8
            default:
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
                break;
        }

        AddCommonTags(dataset, request.Dataset);

        dataset.Add(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid)
              .Add(DicomTag.StudyInstanceUID, series.StudyInstanceUid)
              .Add(DicomTag.Modality, series.Modality ?? string.Empty)
              .Add(DicomTag.SeriesNumber, series.SeriesNumber ?? string.Empty)
              .Add(DicomTag.SeriesDescription, series.SeriesDescription ?? string.Empty);

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

            // 添加字符集和其他用标签
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

        // 使 Task.Run 来异步执行数据库查询
        var instances = await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));

        foreach (var instance in instances)
        {
            try
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                // 添加字符集和其他通用标签
                AddCommonTags(dataset, request.Dataset);

                // 验证 UID 格
                var validStudyUid = ValidateUID(studyInstanceUid);
                var validSeriesUid = ValidateUID(seriesInstanceUid);
                var validSopInstanceUid = ValidateUID(instance.SopInstanceUid);
                var validSopClassUid = ValidateUID(instance.SopClassUid);

                // 置必要的字段
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
                // 跳过空组
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                // 移除前导零并确保至少保留一个数字
                var trimmed = part.TrimStart('0');
                validParts.Add(string.IsNullOrEmpty(trimmed) ? "0" : trimmed);
            }

            // 确保少有两个组件
            if (validParts.Count < 2)
            {
                DicomLogger.Warning("QRSCP", "无效的UID格式 (组件数量不足): {Uid}", 
                    uid ?? string.Empty);
                return "0.0";
            }

            return string.Join(".", validParts);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "UID验证失败: {Uid}", 
                uid ?? string.Empty);
            return "0.0";
        }
    }

    private string GetPreferredCharacterSet(DicomDataset requestDataset)
    {
        // 获取请中的字符集
        var requestedCharacterSet = requestDataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");

        // 如果请求的字符持列表中使用请求的字符集
        if (SupportedCharacterSets.Contains(requestedCharacterSet))
        {
            return requestedCharacterSet;
        }

        // 否则认使用 UTF-8
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
        DicomLogger.Information("QRSCP", "收到C-MOVE请求 - AE: {CallingAE}, 目标: {DestinationAE}", 
            Association.CallingAE, request.DestinationAE);

        // 1. 验证目标 AE
        var moveDestination = _settings.QRSCP.MoveDestinations
            .FirstOrDefault(x => x.AeTitle.Equals(request.DestinationAE, StringComparison.OrdinalIgnoreCase));

        if (moveDestination == null)
        {
            DicomLogger.Warning("QRSCP", "目标AE未配置 - AET: {AET}", request.DestinationAE);
            yield return new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown);
            yield break;
        }

        // 2. 获取实例列表
        var instances = await GetRequestedInstances(request);
        if (!instances.Any())
        {
            DicomLogger.Information("QRSCP", "未找到匹配实例");
            yield return new DicomCMoveResponse(request, DicomStatus.Success);
            yield break;
        }

        // 3. 创建 DICOM 客户端
        var client = CreateDicomClient(moveDestination);

        // 4. 处理实例传输
        var totalInstances = instances.Count();
        var successCount = 0;
        var failedCount = 0;
        var hasNetworkError = false;

        DicomLogger.Information("QRSCP", "开始C-MOVE - 总数: {Total}", totalInstances);

        foreach (var instance in instances)
        {
            if (hasNetworkError) break;

            try
            {
                // 4.1 验证文件存在
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!File.Exists(filePath))
                {
                    DicomLogger.Warning("QRSCP", "文件不存在 - UID: {SopInstanceUid}", instance.SopInstanceUid);
                    failedCount++;
                    continue;
                }

                var file = await DicomFile.OpenAsync(filePath);
                
                try
                {
                    // 4.2 处理传输语法转换
                    var requestedTransferSyntax = GetRequestedTransferSyntax(file);
                    if (requestedTransferSyntax != null && file.Dataset.InternalTransferSyntax != requestedTransferSyntax)
                    {
                        try
                        {
                            var originalSyntax = file.Dataset.InternalTransferSyntax;
                            var transcoder = new DicomTranscoder(originalSyntax, requestedTransferSyntax);
                            var transcodedFile = transcoder.Transcode(file);
                            
                            DicomLogger.Debug("QRSCP", 
                                "语法转换 - UID: {SopInstanceUid}, {Original} -> {Target}", 
                                instance.SopInstanceUid,
                                originalSyntax.UID.Name,
                                requestedTransferSyntax.UID.Name);

                            // 4.3 发送转换后的文件
                            await SendToDestinationAsync(client, transcodedFile);
                        }
                        catch (Exception ex)
                        {
                            DicomLogger.Warning("QRSCP", ex,
                                "语法转换失败，使用原格式 - UID: {SopInstanceUid}", 
                                instance.SopInstanceUid);
                            // 使用原始格式发送
                            await SendToDestinationAsync(client, file);
                        }
                    }
                    else
                    {
                        // 4.3 直接发送原始文件
                        await SendToDestinationAsync(client, file);
                    }

                    successCount++;
                    DicomLogger.Debug("QRSCP", "传输成功 - {Success}/{Total}", successCount, totalInstances);
                }
                catch (Exception ex)
                {
                    if (IsNetworkError(ex))
                    {
                        hasNetworkError = true;
                        DicomLogger.Error("QRSCP", ex, 
                            "网络错误，终止传输 - UID: {SopInstanceUid}", 
                            instance.SopInstanceUid ?? string.Empty);
                        failedCount++;
                        break;
                    }
                    else
                    {
                        failedCount++;
                        DicomLogger.Error("QRSCP", ex, 
                            "传输失败 - UID: {SopInstanceUid}", 
                            instance.SopInstanceUid);
                    }
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                DicomLogger.Error("QRSCP", ex, 
                    "处理失败 - UID: {SopInstanceUid}", 
                    instance.SopInstanceUid);
            }

            // 5. 发送进度响应
            var response = new DicomCMoveResponse(request, DicomStatus.Pending);
            response.Dataset = CreateResponseDataset(totalInstances, successCount, failedCount);
            yield return response;
        }

        // 6. 发送最终状态
        var finalStatus = hasNetworkError ? DicomStatus.QueryRetrieveMoveDestinationUnknown :
                         failedCount > 0 ? DicomStatus.ProcessingFailure :
                         DicomStatus.Success;

        var finalResponse = new DicomCMoveResponse(request, finalStatus);
        finalResponse.Dataset = CreateResponseDataset(totalInstances, successCount, failedCount);

        DicomLogger.Information("QRSCP", 
            "C-MOVE完成 - 总数: {Total}, 成功: {Success}, 失败: {Failed}, 状态: {Status}", 
            totalInstances, successCount, failedCount, finalStatus);

        yield return finalResponse;
    }

    private DicomDataset CreateResponseDataset(int totalInstances, int successCount, int failedCount)
    {
        return new DicomDataset
        {
            { DicomTag.NumberOfRemainingSuboperations, (ushort)(totalInstances - (successCount + failedCount)) },
            { DicomTag.NumberOfCompletedSuboperations, (ushort)successCount },
            { DicomTag.NumberOfFailedSuboperations, (ushort)failedCount },
            { DicomTag.NumberOfWarningSuboperations, (ushort)0 }
        };
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

        // 添加所有可能的存储类 PresentationContexts
        var storageUids = new DicomUID[]
        {
            DicomUID.CTImageStorage,
            DicomUID.MRImageStorage,
            DicomUID.UltrasoundImageStorage,
            DicomUID.SecondaryCaptureImageStorage,
            DicomUID.XRayAngiographicImageStorage,
            DicomUID.XRayRadiofluoroscopicImageStorage,
            DicomUID.DigitalXRayImageStorageForPresentation,
            DicomUID.DigitalMammographyXRayImageStorageForPresentation,
            DicomUID.UltrasoundMultiFrameImageStorage,
            DicomUID.EnhancedCTImageStorage,
            DicomUID.EnhancedMRImageStorage,
            DicomUID.EnhancedXAImageStorage,
            DicomUID.NuclearMedicineImageStorage,
            DicomUID.PositronEmissionTomographyImageStorage
        };

        byte pcid = 1;
        foreach (var uid in storageUids)
        {
            var pc = new DicomPresentationContext(pcid++, uid);
            pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
            client.AdditionalPresentationContexts.Add(pc);
        }

        return client;
    }

    public async IAsyncEnumerable<DicomCGetResponse> OnCGetRequestAsync(DicomCGetRequest request)
    {
        List<DicomCGetResponse> responses = new();
        DicomLogger.Information("QRSCP", "收到C-GET请求 - AE: {CallingAE}, 级别: {Level}", 
            Association.CallingAE, request.Level);

        // 1. 验证请求参数
        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "C-GET缺少StudyInstanceUID");
            responses.Add(new DicomCGetResponse(request, DicomStatus.InvalidAttributeValue));
        }
        else
        {
            // 2. 获取实例列表
            var instances = await GetRequestedInstances(request);
            if (!instances.Any())
            {
                DicomLogger.Information("QRSCP", "未找到匹配实例 - Study: {StudyUid}", studyInstanceUid);
                responses.Add(new DicomCGetResponse(request, DicomStatus.Success));
            }
            else
            {
                try
                {
                    responses.AddRange(await ProcessGetRequestAsync(request, instances));
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("QRSCP", ex, "C-GET处理失败");
                    responses.Add(new DicomCGetResponse(request, DicomStatus.ProcessingFailure));
                }
            }
        }

        foreach (var response in responses)
        {
            yield return response;
        }
    }

    private async Task<List<DicomCGetResponse>> ProcessGetRequestAsync(DicomCGetRequest request, IEnumerable<Instance> instances)
    {
        var responses = new List<DicomCGetResponse>();
        var totalInstances = instances.Count();
        var successCount = 0;
        var failedCount = 0;
        var hasNetworkError = false;

        DicomLogger.Information("QRSCP", "开始C-GET - 总数: {Total}", totalInstances);

        foreach (var instance in instances)
        {
            if (hasNetworkError) break;

            try
            {
                // 1. 验证文件存在
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!File.Exists(filePath))
                {
                    DicomLogger.Warning("QRSCP", "文件不存在 - UID: {SopInstanceUid}", instance.SopInstanceUid);
                    failedCount++;
                    responses.Add(CreateGetProgressResponse(request, totalInstances, successCount, failedCount));
                    continue;
                }

                // 2. 加载DICOM文件
                var file = await DicomFile.OpenAsync(filePath);
                
                try
                {
                    // 3. 创建C-STORE请求
                    var storeRequest = new DicomCStoreRequest(file);

                    // 4. 处理传输语法转换
                    var requestedTransferSyntax = GetRequestedTransferSyntax(file);
                    if (requestedTransferSyntax != null && file.Dataset.InternalTransferSyntax != requestedTransferSyntax)
                    {
                        try
                        {
                            var originalSyntax = file.Dataset.InternalTransferSyntax;
                            var transcoder = new DicomTranscoder(originalSyntax, requestedTransferSyntax);
                            var transcodedFile = transcoder.Transcode(file);
                            
                            DicomLogger.Debug("QRSCP", 
                                "语法转换 - UID: {SopInstanceUid}, {Original} -> {Target}", 
                                instance.SopInstanceUid,
                                originalSyntax.UID.Name,
                                requestedTransferSyntax.UID.Name);

                            // 5. 发送转换后的文件
                            await SendRequestAsync(new DicomCStoreRequest(transcodedFile));
                        }
                        catch (Exception ex)
                        {
                            DicomLogger.Warning("QRSCP", ex,
                                "语法转换失败，使用原格式 - UID: {SopInstanceUid}", 
                                instance.SopInstanceUid);
                            // 使用原始格式发送
                            await SendRequestAsync(new DicomCStoreRequest(file));
                        }
                    }
                    else
                    {
                        // 5. 直接发送原始文件
                        await SendRequestAsync(new DicomCStoreRequest(file));
                    }

                    successCount++;
                    DicomLogger.Debug("QRSCP", "传输成功 - {Success}/{Total}", successCount, totalInstances);
                }
                catch (Exception ex)
                {
                    if (IsNetworkError(ex))
                    {
                        hasNetworkError = true;
                        DicomLogger.Error("QRSCP", ex, 
                            "网络错误，终止传输 - UID: {SopInstanceUid}", 
                            instance.SopInstanceUid);
                        failedCount++;
                        responses.Add(CreateGetProgressResponse(request, totalInstances, successCount, failedCount, DicomStatus.ProcessingFailure));
                        break;
                    }
                    else
                    {
                        failedCount++;
                        DicomLogger.Error("QRSCP", ex, 
                            "传输失败 - UID: {SopInstanceUid}", 
                            instance.SopInstanceUid);
                    }
                }

                if (!hasNetworkError)
                {
                    responses.Add(CreateGetProgressResponse(request, totalInstances, successCount, failedCount));
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                DicomLogger.Error("QRSCP", ex, 
                    "处理失败 - UID: {SopInstanceUid}", 
                    instance.SopInstanceUid);
                responses.Add(CreateGetProgressResponse(request, totalInstances, successCount, failedCount));
            }
        }

        // 发送最终状态
        if (!hasNetworkError)
        {
            var finalStatus = failedCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success;
            responses.Add(CreateGetProgressResponse(request, totalInstances, successCount, failedCount, finalStatus));
        }

        DicomLogger.Information("QRSCP", 
            "C-GET完成 - 总数: {Total}, 成功: {Success}, 失败: {Failed}", 
            totalInstances, successCount, failedCount);

        return responses;
    }

    private async Task ProcessDicomFileAsync(IDicomClient client, DicomFile file, string sopInstanceUid)
    {
        try
        {
            var requestedTransferSyntax = GetRequestedTransferSyntax(file);
            var currentTransferSyntax = file.Dataset.InternalTransferSyntax;

            // 如果当前传输语法与请求的不同，需要转换
            if (currentTransferSyntax != requestedTransferSyntax)
            {
                try
                {
                    var transcoder = new DicomTranscoder(currentTransferSyntax, requestedTransferSyntax);
                    var transcodedFile = transcoder.Transcode(file);

                    DicomLogger.Information("QRSCP", 
                        "传输语法转换 - 实例: {SopInstanceUid}\n  原始语法: {Original}\n  目标语法: {Target}", 
                        sopInstanceUid,
                        GetTransferSyntaxName(currentTransferSyntax),
                        GetTransferSyntaxName(requestedTransferSyntax));

                    await SendToDestinationAsync(client, transcodedFile);
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("QRSCP", ex, 
                        "传输语法转换失败，使用原始格式 - 实例: {SopInstanceUid}", 
                        sopInstanceUid);

                    await SendToDestinationAsync(client, file);
                }
            }
            else
            {
                await SendToDestinationAsync(client, file);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, 
                "处理DICOM文件失败 - 实例: {SopInstanceUid}", 
                sopInstanceUid);
            throw;
        }
    }

    private async Task SendToDestinationAsync(IDicomClient client, DicomFile file)
    {
        var sopClassUid = file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);
        var presentationContext = new DicomPresentationContext(
            (byte)client.AdditionalPresentationContexts.Count(),
            sopClassUid);

        presentationContext.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
        client.AdditionalPresentationContexts.Add(presentationContext);

        await client.AddRequestAsync(new DicomCStoreRequest(file));
        await client.SendAsync();
    }

    private DicomCGetResponse CreateGetProgressResponse(
        DicomCGetRequest request, 
        int totalInstances, 
        int successCount, 
        int failedCount,
        DicomStatus? status = null)
    {
        var remaining = totalInstances - (successCount + failedCount);
        var currentStatus = status ?? (remaining > 0 ? DicomStatus.Pending : 
            failedCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success);

        return new DicomCGetResponse(request, currentStatus)
        {
            Dataset = new DicomDataset()
                .Add(DicomTag.NumberOfRemainingSuboperations, (ushort)remaining)
                .Add(DicomTag.NumberOfCompletedSuboperations, (ushort)successCount)
                .Add(DicomTag.NumberOfFailedSuboperations, (ushort)failedCount)
        };
    }

    private async Task SendDicomFileAsync(DicomFile file)
    {
        try 
        {
            // 创建 C-STORE 请求
            var request = new DicomCStoreRequest(file);

            // 获取客户端请求的传输语法
            var requestedTransferSyntax = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID))
                ?.AcceptedTransferSyntax;

            DicomLogger.Information("QRSCP", 
                "传输语法协 - 原始: {Original}, 请求: {Requested}", 
                file.Dataset.InternalTransferSyntax.UID.Name,
                requestedTransferSyntax?.UID.Name ?? "未指定");

            if (requestedTransferSyntax != null && file.Dataset.InternalTransferSyntax != requestedTransferSyntax)
            {
                try
                {
                    // 使用 DicomTranscoder 进行转换
                    var transcoder = new DicomTranscoder(
                        file.Dataset.InternalTransferSyntax, 
                        requestedTransferSyntax);
                    var transcodedFile = transcoder.Transcode(file);
                    file = transcodedFile;

                    DicomLogger.Information("QRSCP",
                        "传输语法转换成功 - 原格式: {OriginalSyntax} -> 新格式: {NewSyntax}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        requestedTransferSyntax.UID.Name);
                }
                catch (DicomCodecException ex)
                {
                    DicomLogger.Warning("QRSCP", 
                        "传输语法转换失败，使用默认格式 - 原格式: {Original}, 目标: {Target}, 错误: {Error}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        requestedTransferSyntax.UID.Name,
                        ex.Message);
                }
            }

            await SendRequestAsync(request);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "发送DICOM文件失败");
            throw;
        }
    }

    private async Task<DicomFile?> GetDicomFileAsync(string filePath, DicomCMoveRequest request)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                DicomLogger.Warning("QRSCP", "文件不存在 - 路径: {FilePath}", filePath);
                return null;
            }

            var file = await DicomFile.OpenAsync(filePath);
            var sopClassUid = file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);

            // 从C-MOVE请求的PresentationContext中获取传输语法
            var requestedTransferSyntax = request.PresentationContext?.AcceptedTransferSyntax;
            var supportedTransferSyntaxes = request.PresentationContext?.GetTransferSyntaxes();

            DicomLogger.Information("QRSCP", 
                "传输语法协商 - 文件: {FilePath}\n原始语法: {Original}\n请求语法: {Requested}\n支持的语法: {Supported}", 
                filePath,
                file.Dataset.InternalTransferSyntax.UID.Name,
                requestedTransferSyntax?.UID.Name ?? "未指定",
                supportedTransferSyntaxes != null ? 
                    string.Join(", ", supportedTransferSyntaxes.Select(ts => ts.UID.Name)) : 
                    "未指定");

            // 如果需要转换传输语法
            if (requestedTransferSyntax != null && file.Dataset.InternalTransferSyntax != requestedTransferSyntax)
            {
                try
                {
                    var transcoder = new DicomTranscoder(
                        file.Dataset.InternalTransferSyntax, 
                        requestedTransferSyntax);
                    var transcodedFile = transcoder.Transcode(file);
                    return transcodedFile;
                }
                catch (DicomCodecException ex)
                {
                    DicomLogger.Warning("QRSCP", 
                        "传输语法转换失败 - 原格式: {Original}, 目标: {Target}, 错误: {Error}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        requestedTransferSyntax.UID.Name,
                        ex.Message);
                }
            }

            return file;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "获取DICOM文件失败 - 路径: {FilePath}", filePath);
            return null;
        }
    }

    public async Task OnCMoveRequestAsync(DicomCMoveRequest request, DicomCMoveResponse response)
    {
        try
        {
            var destination = request.DestinationAE;
            var moveDestination = _settings.QRSCP.MoveDestinations
                .FirstOrDefault(x => x.AeTitle.Equals(destination, StringComparison.OrdinalIgnoreCase));

            if (moveDestination == null)
            {
                DicomLogger.Warning("QRSCP", "未找到目标AE - AET: {AET}", destination);
                response.Status = DicomStatus.QueryRetrieveMoveDestinationUnknown;
                return;
            }

            // 创建C-STORE客户端
            var client = CreateDicomClient(moveDestination);

            // 查询需要传输的实例
            var instances = await GetRequestedInstances(request);
            if (!instances.Any())
            {
                DicomLogger.Warning("QRSCP", "未找到匹配的实例");
                response.Status = DicomStatus.QueryRetrieveUnableToProcess;
                return;
            }

            int successCount = 0;
            int failureCount = 0;
            int remainingCount = instances.Count();

            foreach (var instance in instances)
            {
                try
                {
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    var file = await GetDicomFileAsync(filePath, request);
                    if (file == null)
                    {
                        failureCount++;
                        remainingCount--;
                        continue;
                    }

                    var storeRequest = new DicomCStoreRequest(file);
                    
                    // 设置传输语法
                    var sopClassUid = file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);
                    var presentationContext = new DicomPresentationContext(
                        (byte)client.AdditionalPresentationContexts.Count(),
                        sopClassUid);

                    // 使用目标节点支持的传输语法
                    presentationContext.AcceptTransferSyntaxes(
                        DicomTransferSyntax.JPEGLSLossless,
                        DicomTransferSyntax.JPEG2000Lossless,
                        DicomTransferSyntax.JPEGProcess14SV1,
                        DicomTransferSyntax.RLELossless,
                        DicomTransferSyntax.JPEGLSNearLossless,
                        DicomTransferSyntax.JPEG2000Lossy,
                        DicomTransferSyntax.JPEGProcess1,
                        DicomTransferSyntax.JPEGProcess2_4,
                        DicomTransferSyntax.ExplicitVRLittleEndian,
                        DicomTransferSyntax.ImplicitVRLittleEndian,
                        DicomTransferSyntax.ExplicitVRBigEndian
                    );
                    client.AdditionalPresentationContexts.Add(presentationContext);

                    await client.AddRequestAsync(storeRequest);
                    await client.SendAsync();
                    successCount++;
                    remainingCount--;
                    
                    DicomLogger.Information("QRSCP", 
                        "实例发送成功 - SOPInstanceUID: {SopInstanceUid}, 进度: {Success}/{Total}", 
                        instance.SopInstanceUid, successCount, instances.Count());

                    // 更新应状态
                    response.Status = DicomStatus.Pending;
                    if (response.Dataset == null)
                    {
                        response.Dataset = new DicomDataset();
                    }
                    response.Dataset.AddOrUpdate(DicomTag.NumberOfRemainingSuboperations, remainingCount);
                    response.Dataset.AddOrUpdate(DicomTag.NumberOfCompletedSuboperations, successCount);
                    response.Dataset.AddOrUpdate(DicomTag.NumberOfFailedSuboperations, failureCount);
                }
                catch (Exception ex)
                {
                    failureCount++;
                    remainingCount--;
                    DicomLogger.Error("QRSCP", ex, 
                        "发送实例失败 - SOPInstanceUID: {SopInstanceUid}", 
                        instance.SopInstanceUid);

                    if (IsNetworkError(ex))
                    {
                        response.Status = DicomStatus.QueryRetrieveMoveDestinationUnknown;
                        if (response.Dataset == null)
                        {
                            response.Dataset = new DicomDataset();
                        }
                        response.Dataset.AddOrUpdate(DicomTag.NumberOfRemainingSuboperations, remainingCount);
                        response.Dataset.AddOrUpdate(DicomTag.NumberOfCompletedSuboperations, successCount);
                        response.Dataset.AddOrUpdate(DicomTag.NumberOfFailedSuboperations, failureCount);
                        return;
                    }
                }
            }

            // 设置最终状态
            response.Status = failureCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success;
            if (response.Dataset == null)
            {
                response.Dataset = new DicomDataset();
            }
            response.Dataset.AddOrUpdate(DicomTag.NumberOfRemainingSuboperations, 0);
            response.Dataset.AddOrUpdate(DicomTag.NumberOfCompletedSuboperations, successCount);
            response.Dataset.AddOrUpdate(DicomTag.NumberOfFailedSuboperations, failureCount);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "C-MOVE请求处理失败");
            response.Status = DicomStatus.QueryRetrieveUnableToProcess;
            if (response.Dataset == null)
            {
                response.Dataset = new DicomDataset();
            }
            response.Dataset.AddOrUpdate(DicomTag.NumberOfFailedSuboperations, 1);
        }
    }

    private async Task<IEnumerable<Instance>> GetRequestedInstances(DicomRequest request)
    {
        try
        {
            var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
            var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
            var sopInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, string.Empty);

            DicomLogger.Information("QRSCP", 
                "获取实例列表 - Study: {StudyUid}, Series: {SeriesUid}, SOP: {SopUid}",
                studyInstanceUid, seriesInstanceUid, sopInstanceUid);

            if (!string.IsNullOrEmpty(sopInstanceUid))
            {
                var instance = await Task.Run(() => _repository.GetInstanceAsync(sopInstanceUid));
                return instance != null ? new[] { instance } : Array.Empty<Instance>();
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
            return Array.Empty<Instance>();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "获取实例列表失败");
            return Array.Empty<Instance>();
        }
    }

    private async Task<List<DicomCFindResponse>> HandlePatientLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // 从请求中获取查询参数
            var patientId = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty);
            var patientName = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty);

            DicomLogger.Information("QRSCP", 
                "Patient级查询 - ID: {PatientId}, Name: {PatientName}", 
                patientId, patientName);

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
                      .Add(DicomTag.PatientSex, patient.PatientSex ?? string.Empty)
                      .Add(DicomTag.NumberOfPatientRelatedStudies, patient.NumberOfStudies.ToString())
                      .Add(DicomTag.NumberOfPatientRelatedSeries, patient.NumberOfSeries.ToString())
                      .Add(DicomTag.NumberOfPatientRelatedInstances, patient.NumberOfInstances.ToString());

                response.Dataset = dataset;
                responses.Add(response);
            }

            DicomLogger.Information("QRSCP", "Patient级查询完成 - 返回记录数: {Count}", responses.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Patient级查询失败");
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }

    private bool IsNetworkError(Exception ex)
    {
        if (ex == null) return false;

        // 检查是否是网络相关的异常
        return ex is System.Net.Sockets.SocketException ||
               ex is System.Net.WebException ||
               ex is System.IO.IOException ||
               ex is DicomNetworkException ||
               (ex.InnerException != null && IsNetworkError(ex.InnerException));
    }

    private DicomTransferSyntax? GetRequestedTransferSyntax(DicomFile file)
    {
        try
        {
            // 1. 从 C-MOVE/C-GET 服务的 Presentation Context 中获取传输语法
            var moveContext = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                                   pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove);

            var moveTransferSyntax = moveContext?.AcceptedTransferSyntax;
            if (moveTransferSyntax != null)
            {
                var (isSourceCompressed, sourceType) = GetCompressionInfo(file.Dataset.InternalTransferSyntax);
                var (isTargetCompressed, targetType) = GetCompressionInfo(moveTransferSyntax);

                if (!_transferSyntaxLogged)
                {
                    DicomLogger.Information("QRSCP", 
                        "传输语法检查 - 本地文件: {SourceType} ({SourceSyntax}), 请求格式: {TargetType} ({TargetSyntax})", 
                        isSourceCompressed ? sourceType : "未压缩",
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        isTargetCompressed ? targetType : "未压缩",
                        moveTransferSyntax.UID.Name);
                    _transferSyntaxLogged = true;
                }

                return moveTransferSyntax;
            }

            // 2. 获取当前文件的 SOP Class UID 对应的传输语法
            var sopClassUid = file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);
            var storageContext = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == sopClassUid);

            var storageTransferSyntax = storageContext?.AcceptedTransferSyntax;
            if (storageTransferSyntax != null)
            {
                var (isSourceCompressed, sourceType) = GetCompressionInfo(file.Dataset.InternalTransferSyntax);
                var (isTargetCompressed, targetType) = GetCompressionInfo(storageTransferSyntax);

                if (!_transferSyntaxLogged)
                {
                    DicomLogger.Information("QRSCP", 
                        "存储类传输语法检查 - 本地文件: {SourceType} ({SourceSyntax}), 请求格式: {TargetType} ({TargetSyntax})", 
                        isSourceCompressed ? sourceType : "未压缩",
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        isTargetCompressed ? targetType : "未压缩",
                        storageTransferSyntax.UID.Name);
                    _transferSyntaxLogged = true;
                }

                return storageTransferSyntax;
            }

            // 3. 如果都没有指定传输语法，使用默认的传输语法
            DicomLogger.Information("QRSCP", "未指定传输语法，使用默认传输语法: Explicit VR Little Endian");
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "获取请求的传输语法失败，使用默认传输语法: Explicit VR Little Endian");
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
    }

    /// <summary>
    /// 检查传输语法是否为压缩格式，并返回压缩类型
    /// </summary>
    private (bool isCompressed, string compressionType) GetCompressionInfo(DicomTransferSyntax syntax)
    {
        if (syntax == DicomTransferSyntax.JPEGProcess1 ||
            syntax == DicomTransferSyntax.JPEGProcess2_4)
            return (true, "JPEG Baseline");
        
        if (syntax == DicomTransferSyntax.JPEGProcess14 ||
            syntax == DicomTransferSyntax.JPEGProcess14SV1)
            return (true, "JPEG Lossless");
        
        if (syntax == DicomTransferSyntax.JPEGLSLossless ||
            syntax == DicomTransferSyntax.JPEGLSNearLossless)
            return (true, "JPEG-LS");
        
        if (syntax == DicomTransferSyntax.JPEG2000Lossless)
            return (true, "JPEG2000 Lossless");
        
        if (syntax == DicomTransferSyntax.JPEG2000Lossy)
            return (true, "JPEG2000 Lossy");
        
        if (syntax == DicomTransferSyntax.RLELossless)
            return (true, "RLE");
        
        if (syntax == DicomTransferSyntax.DeflatedExplicitVRLittleEndian)
            return (true, "Deflated");

        return (false, string.Empty);
    }

    /// <summary>
    /// 获取传输语法的友好名称
    /// </summary>
    private string GetTransferSyntaxName(DicomTransferSyntax? syntax)
    {
        if (syntax == null)
        {
            return "未知传输语法";
        }

        // 判断压缩类型
        var (_, compressionType) = GetCompressionInfo(syntax);
        string compressionDesc = string.IsNullOrEmpty(compressionType) ? "" : compressionType;
        
        return syntax.UID.Name switch
        {
            "1.2.840.10008.1.2" => "隐式VR小端",
            "1.2.840.10008.1.2.1" => "显式VR小端",
            "1.2.840.10008.1.2.2" => "显式VR大端",
            "1.2.840.10008.1.2.4.70" => $"JPEG {compressionDesc}",
            "1.2.840.10008.1.2.4.57" => $"JPEG P14 {compressionDesc}",
            "1.2.840.10008.1.2.4.80" => $"JPEG-LS {compressionDesc}",
            "1.2.840.10008.1.2.4.81" => "JPEG-LS 近无损",
            "1.2.840.10008.1.2.4.90" => $"JPEG 2000 {compressionDesc}",
            "1.2.840.10008.1.2.4.91" => $"JPEG 2000 {compressionDesc}",
            "1.2.840.10008.1.2.5" => "RLE 无损",
            _ => syntax.UID.Name
        };
    }

    // 实现 IDicomCStoreProvider 接口
    public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        DicomLogger.Warning("QRSCP", "不支持直接存储服务 - AET: {CallingAE}, SOPInstanceUID: {SopInstanceUid}", 
            Association?.CallingAE ?? "Unknown",
            request.SOPInstanceUID?.ToString() ?? string.Empty);

        // 返回不支持的状态
        return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.SOPClassNotSupported));
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        DicomLogger.Warning("QRSCP", e, "存储请求异常 - 临时文件: {TempFile}", 
            tempFileName ?? string.Empty);
        return Task.CompletedTask;
    }
}


