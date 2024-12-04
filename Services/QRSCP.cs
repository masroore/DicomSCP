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
        DicomTransferSyntax.JPEGLSLossless,            // JPEG-LS Lossless
        DicomTransferSyntax.JPEG2000Lossless,          // JPEG 2000 Lossless
        DicomTransferSyntax.JPEGProcess14SV1,          // JPEG Lossless
        DicomTransferSyntax.RLELossless,               // RLE Lossless
        DicomTransferSyntax.JPEGLSNearLossless,        // JPEG-LS Near Lossless
        DicomTransferSyntax.JPEG2000Lossy,             // JPEG 2000 Lossy
        DicomTransferSyntax.JPEGProcess1,              // JPEG Lossy Process 1
        DicomTransferSyntax.JPEGProcess2_4,            // JPEG Lossy Process 2-4
        DicomTransferSyntax.ExplicitVRLittleEndian,    // Explicit Little Endian
        DicomTransferSyntax.ImplicitVRLittleEndian,    // Implicit Little Endian
        DicomTransferSyntax.ExplicitVRBigEndian        // Explicit Big Endian
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
                    pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)             // Storage
                {
                    DicomLogger.Information("QRSCP", "接受服务 - AET: {CallingAE}, 服务: {Service}", 
                        association.CallingAE, pc.AbstractSyntax.Name);

                    // 根据服务类型选择合适的传输语法
                    if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                    {
                        // 对于存储类服务，接受所有支持的传输语法
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
                        DicomLogger.Information("QRSCP", "为存储类服务接受传输语法 - 服务: {Service}, 支持的语法数: {Count}", 
                            pc.AbstractSyntax.Name, AcceptedImageTransferSyntaxes.Length);
                    }
                    else if (pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove)
                    {
                        // 对于 C-MOVE 服务，同时接受基本传输语法和图像传输语法
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes.Concat(AcceptedTransferSyntaxes).Distinct().ToArray());
                        DicomLogger.Information("QRSCP", "为C-MOVE服务接受传输语法 - 服务: {Service}", pc.AbstractSyntax.Name);
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

        // 使用 fo-dicom 的查询理
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
            
            // 如果是单个日期，开始和结束日期相同
            return (cleanDateValue, cleanDateValue);
        }

        var dateRange = ProcessDateRange(studyDate);
        DicomLogger.Debug("QRSCP", "日期范围处理: 原始值={Original}, 开始={Start}, 结束={End}", 
            studyDate,
            dateRange.StartDate,
            dateRange.EndDate);

        // 处理 Modality 列表
        string[] ProcessModalities(DicomDataset dataset)
        {
            var modalities = new List<string>();

            // 尝试获取 ModalitiesInStudy
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
              .Add(DicomTag.StudyDescription, study.StudyDescription ?? string.Empty)
              .Add(DicomTag.ModalitiesInStudy, study.Modality ?? string.Empty)
              .Add(DicomTag.AccessionNumber, study.AccessionNumber ?? string.Empty);

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

            // 确保至少有两个组件
            if (validParts.Count < 2)
            {
                DicomLogger.Warning("QRSCP", "无效的UID格 (组件数量不足): {Uid}", uid);
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
        DicomLogger.Information("QRSCP", "收到 C-MOVE 请求 - 来自: {CallingAE}, 目标: {DestinationAE}, 级别: {Level}", 
            Association.CallingAE, request.DestinationAE, request.Level);

        // 获取 C-MOVE 服务的传输语法
        var moveContext = Association.PresentationContexts
            .FirstOrDefault(pc => pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                                pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove);

        if (moveContext != null)
        {
            DicomLogger.Information("QRSCP", "C-MOVE 服务使用的传输语法: {TransferSyntax}", 
                moveContext.AcceptedTransferSyntax?.UID.Name ?? "未指定");
        }

        // 记录关联的 Presentation Contexts
        if (Association.PresentationContexts != null && Association.PresentationContexts.Count > 0)
        {
            DicomLogger.Information("QRSCP", "关联的 Presentation Contexts:");
            foreach (var context in Association.PresentationContexts)
            {
                DicomLogger.Information("QRSCP", "- SOP Class UID: {AbstractSyntax}", context.AbstractSyntax.UID);
                DicomLogger.Information("QRSCP", "  传输语法: {TransferSyntax}", 
                    context.AcceptedTransferSyntax?.UID.Name ?? "未接受");
            }
        }
        else
        {
            DicomLogger.Warning("QRSCP", "关联中未找到 Presentation Context");
        }

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
                    { DicomTag.NumberOfRemainingSuboperations, (ushort)0 },
                    { DicomTag.NumberOfCompletedSuboperations, (ushort)0 },
                    { DicomTag.NumberOfFailedSuboperations, (ushort)instances.Count() }
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

                var file = await DicomFile.OpenAsync(filePath);
                var sopClassUid = file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);

                // 获取客户端请求的传输语法
                var requestedTransferSyntax = moveContext?.AcceptedTransferSyntax;
                if (requestedTransferSyntax != null)
                {
                    DicomLogger.Information("QRSCP", 
                        "准备转换传输语法 - 文件: {FilePath}, 原始语法: {Original}, 目标语法: {Target}", 
                        filePath,
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        requestedTransferSyntax.UID.Name);

                    if (file.Dataset.InternalTransferSyntax != requestedTransferSyntax)
                    {
                        try
                        {
                            var transcoder = new DicomTranscoder(
                                file.Dataset.InternalTransferSyntax,
                                requestedTransferSyntax);
                            file = transcoder.Transcode(file);

                            DicomLogger.Information("QRSCP", 
                                "传输语法转换成功 - 从 {Original} 到 {Target}", 
                                file.Dataset.InternalTransferSyntax.UID.Name,
                                requestedTransferSyntax.UID.Name);
                        }
                        catch (Exception ex)
                        {
                            DicomLogger.Warning("QRSCP", ex,
                                "传输语法转换失败，尝试使用默认语法 - 原格式: {Original}, 目标: {Target}", 
                                file.Dataset.InternalTransferSyntax.UID.Name,
                                requestedTransferSyntax.UID.Name);

                            // 如果转换失败，尝试转换为 Explicit VR Little Endian
                            if (file.Dataset.InternalTransferSyntax.IsEncapsulated)
                            {
                                var defaultTranscoder = new DicomTranscoder(
                                    file.Dataset.InternalTransferSyntax,
                                    DicomTransferSyntax.ExplicitVRLittleEndian);
                                file = defaultTranscoder.Transcode(file);
                            }
                        }
                    }
                }

                var storeRequest = new DicomCStoreRequest(file);
                
                // 设置传输语法
                var presentationContext = new DicomPresentationContext(
                    (byte)client.AdditionalPresentationContexts.Count(),
                    sopClassUid);

                // 优先使用客户端请求的传输语法
                if (requestedTransferSyntax != null)
                {
                    presentationContext.AcceptTransferSyntaxes(requestedTransferSyntax);
                    DicomLogger.Information("QRSCP", 
                        "使用请求的传输语法 - 实例: {SopInstanceUid}, 传输语法: {TransferSyntax}", 
                        instance.SopInstanceUid,
                        requestedTransferSyntax.UID.Name);
                }
                else
                {
                    presentationContext.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
                    DicomLogger.Information("QRSCP", 
                        "使用默认传输语法列表 - 实例: {SopInstanceUid}", 
                        instance.SopInstanceUid);
                }

                client.AdditionalPresentationContexts.Add(presentationContext);

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
                            { DicomTag.NumberOfRemainingSuboperations, (ushort)(instances.Count() - (successCount + 1)) },
                            { DicomTag.NumberOfCompletedSuboperations, (ushort)successCount },
                            { DicomTag.NumberOfFailedSuboperations, (ushort)(instances.Count() - successCount) }
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

            var pendingResponse = new DicomCMoveResponse(request, DicomStatus.Pending)
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.NumberOfRemainingSuboperations, (ushort)(instances.Count() - (successCount + failedCount)) },
                    { DicomTag.NumberOfCompletedSuboperations, (ushort)successCount },
                    { DicomTag.NumberOfFailedSuboperations, (ushort)failedCount }
                }
            };

            yield return pendingResponse;
        }

        var finalStatus = failedCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success;
        var finalResponse = new DicomCMoveResponse(request, finalStatus)
        {
            Dataset = new DicomDataset
            {
                { DicomTag.NumberOfRemainingSuboperations, (ushort)0 },
                { DicomTag.NumberOfCompletedSuboperations, (ushort)successCount },
                { DicomTag.NumberOfFailedSuboperations, (ushort)failedCount }
            }
        };

        yield return finalResponse;
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
        DicomLogger.Information("QRSCP", "收到 C-GET 请求 - 来自: {CallingAE}, 级别: {Level}", 
            Association.CallingAE, request.Level);

        // 获取实例列表
        var instances = await GetRequestedInstances(request);
        if (!instances.Any())
        {
            yield return new DicomCGetResponse(request, DicomStatus.Success);
            yield break;
        }

        DicomLogger.Information("QRSCP", "始 C-GET 操作 - 总实例数: {Total}", instances.Count());

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
                "传输语法协商 - 原始: {Original}, 请求: {Requested}", 
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
                    
                    // 如果转换失败，使用默认格式
                    if (file.Dataset.InternalTransferSyntax.IsEncapsulated)
                    {
                        var defaultTranscoder = new DicomTranscoder(
                            file.Dataset.InternalTransferSyntax,
                            DicomTransferSyntax.ExplicitVRLittleEndian);
                        file = defaultTranscoder.Transcode(file);
                    }
                }
            }
            else if (requestedTransferSyntax == null)
            {
                // 客户端没有指定传输语法，使用默认的传输语法列表
                request.PresentationContext.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
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
}