using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using DicomSCP.Data;
using DicomSCP.Models;
using DicomSCP.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace DicomSCP.Services;

public record WorklistQueryParameters(
    string PatientId,
    string AccessionNumber,
    (string StartDate, string EndDate) DateRange,
    string Modality,
    string ScheduledStationName);

public class WorklistSCP : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCEchoProvider
{
    private static DicomSettings? _settings;
    private static DicomRepository? _repository;

    public static void Configure(
        DicomSettings settings,
        IConfiguration configuration,
        DicomRepository repository)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public WorklistSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log, 
        DicomServiceDependencies dependencies,
        IOptions<DicomSettings> settings)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        if (settings?.Value == null || dependencies?.LoggerFactory == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        _settings = settings.Value;
        DicomLogger.Information("服务初始化完成");
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("WorklistSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Information("WorklistSCP", "连接正常关闭");
        }
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        var calledAE = association.CalledAE;
        var expectedAE = _settings?.WorklistSCP.AeTitle ?? string.Empty;

        if (!string.Equals(expectedAE, calledAE, StringComparison.OrdinalIgnoreCase))
        {
            DicomLogger.Warning("WorklistSCP", "拒绝错误的 Called AE: {CalledAE}, 期望: {ExpectedAE}", 
                calledAE, expectedAE);
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.CalledAENotRecognized);
        }

        if (string.IsNullOrEmpty(association.CallingAE))
        {
            DicomLogger.Warning("WorklistSCP", "拒绝空的 Calling AE");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.CallingAENotRecognized);
        }

        if (_settings?.WorklistSCP.ValidateCallingAE == true)
        {
            if (!_settings.WorklistSCP.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("WorklistSCP", "拒绝未授权的调用方AE: {CallingAE}", association.CallingAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }
        }

        DicomLogger.Information("WorklistSCP", "验证通过 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            calledAE, association.CallingAE);

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind ||
                pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(
                    DicomTransferSyntax.ImplicitVRLittleEndian,
                    DicomTransferSyntax.ExplicitVRLittleEndian,
                    DicomTransferSyntax.ExplicitVRBigEndian);
                DicomLogger.Information("WorklistSCP", "接受服务 - AET: {CallingAE}, 服务: {Service}", 
                    association.CallingAE, pc.AbstractSyntax.Name);
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                DicomLogger.Warning("WorklistSCP", "拒绝不支持的服务 - AET: {CallingAE}, AbstractSyntax: {AbstractSyntax}", 
                    association.CallingAE, pc.AbstractSyntax);
            }
        }

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("接收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("接收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        if (_settings == null)
        {
            DicomLogger.Error("WorklistSCP", null, "服务未配置");
            yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
            yield break;
        }

        DicomLogger.Information("WorklistSCP", "收到工作列表查询请求 - 原始数据集: {@Dataset}", 
            request.Dataset.ToDictionary(x => x.Tag.ToString(), x => x.ToString()));

        var responses = await Task.Run(() => ProcessWorklistQuery(request));
        foreach (var response in responses)
        {
            yield return response;
        }
    }

    private IEnumerable<DicomCFindResponse> ProcessWorklistQuery(DicomCFindRequest request)
    {
        List<WorklistItem> worklistItems;
        try
        {
            var parameters = ExtractQueryParameters(request);

            worklistItems = _repository?.GetWorklistItems(
                parameters.PatientId,
                parameters.AccessionNumber,
                parameters.DateRange,
                parameters.Modality,
                parameters.ScheduledStationName) ?? new List<WorklistItem>();

            DicomLogger.Information("WorklistSCP", "查询到工作列表项: {Count} 条记录", worklistItems.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "工作列表查询失败: {Message}", ex.Message);
            return new[] { new DicomCFindResponse(request, DicomStatus.ProcessingFailure) };
        }

        if (worklistItems.Count == 0)
        {
            DicomLogger.Information("WorklistSCP", "未找到匹配的工作列表项");
            return new[] { new DicomCFindResponse(request, DicomStatus.Success) };
        }

        var responses = new List<DicomCFindResponse>();
        var hasErrors = false;

        foreach (var item in worklistItems)
        {
            try
            {
                var response = CreateWorklistResponse(request, item);
                responses.Add(response);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WorklistSCP", ex, "创建响应失败 - PatientId: {PatientId}", item.PatientId);
                hasErrors = true;
            }
        }

        if (responses.Count == 0 && hasErrors)
        {
            DicomLogger.Error("WorklistSCP", null, "所有响应创建都失败");
            return new[] { new DicomCFindResponse(request, DicomStatus.ProcessingFailure) };
        }

        DicomLogger.Information("WorklistSCP", "工作列表查询完成 - 返回记录数: {Count}, 是否有错误: {HasErrors}", 
            responses.Count, hasErrors);
        responses.Add(new DicomCFindResponse(request, DicomStatus.Success));
        return responses;
    }

    private List<WorklistItem> QueryWorklistItems(
        (string PatientId, string AccessionNumber, string ScheduledDateTime, string Modality, string ScheduledStationName) filters)
    {
        if (_repository == null)
        {
            DicomLogger.Error("WorklistSCP", null, "数据仓储未配置");
            throw new InvalidOperationException("Repository not configured");
        }

        try
        {
            DicomLogger.Debug("WorklistSCP", "执行工作列表查询");
            return _repository.GetWorklistItems(
                filters.PatientId,
                filters.AccessionNumber,
                (filters.ScheduledDateTime, filters.ScheduledDateTime),
                filters.Modality,
                filters.ScheduledStationName);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "查询工作列表失败 - 查询条件: {@Filters}", filters);
            throw; // 让上层处理错误
        }
    }

    private DicomCFindResponse CreateWorklistResponse(DicomCFindRequest request, WorklistItem item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        try
        {
            var dataset = new DicomDataset();
            
            // 获取请求中的字符集，如果没有指定则默认使用 UTF-8
            var requestedCharacterSet = request.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");
            DicomLogger.Debug("WorklistSCP", "请求的字符集: {CharacterSet}", requestedCharacterSet);
            
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

            // 患者信息
            dataset.Add(DicomTag.PatientID, item.PatientId);
            dataset.Add(DicomTag.PatientName, item.PatientName);
            // 确保出生日期格式正确
            try
            {
                var birthDate = DateTime.ParseExact(item.PatientBirthDate, "yyyyMMdd", null);
                dataset.Add(DicomTag.PatientBirthDate, birthDate.ToString("yyyyMMdd"));
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("WorklistSCP", "处理出生日期失败 - PatientId: {PatientId}, BirthDate: {BirthDate}, Error: {Error}", 
                    item.PatientId, item.PatientBirthDate, ex.Message);
                dataset.Add(DicomTag.PatientBirthDate, "19000101");  // 使用默认值
            }
            dataset.Add(DicomTag.PatientSex, item.PatientSex);
            
            // 添加年龄信息
            if (!string.IsNullOrEmpty(item.PatientBirthDate))
            {
                try
                {
                    var birthDate = DateTime.ParseExact(item.PatientBirthDate, "yyyyMMdd", null);
                    var age = DateTime.Now.Year - birthDate.Year;
                    if (DateTime.Now.DayOfYear < birthDate.DayOfYear)
                    {
                        age--;
                    }
                    dataset.Add(DicomTag.PatientAge, $"{age:000}Y");  // 格式化为 "045Y" 这样的格式
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("WorklistSCP", "计算年龄失败 - PatientId: {PatientId}, BirthDate: {BirthDate}, Error: {Error}", 
                        item.PatientId, item.PatientBirthDate, ex.Message);
                }
            }

            // 研究信息
            dataset.Add(DicomTag.StudyInstanceUID, item.StudyInstanceUid);
            dataset.Add(DicomTag.AccessionNumber, item.AccessionNumber);
            dataset.Add(DicomTag.ReferringPhysicianName, item.ReferringPhysicianName);
            dataset.Add(DicomTag.StudyDescription, item.StudyDescription);

            // 检查部位信息
            dataset.Add(DicomTag.BodyPartExamined, item.BodyPartExamined ?? "");
            dataset.Add(DicomTag.RequestedProcedureDescription, item.RequestedProcedureDescription);
            dataset.Add(DicomTag.ScheduledProcedureStepDescription, item.ScheduledProcedureStepDescription);
            dataset.Add(DicomTag.ReasonForTheRequestedProcedure, item.ReasonForRequest ?? "");

            // 预约信息
            dataset.Add(DicomTag.Modality, item.Modality);
            dataset.Add(DicomTag.ScheduledStationAETitle, item.ScheduledAET);

            // 处理预约日期时间
            try
            {
                var scheduledDateTime = DateTime.Parse(item.ScheduledDateTime);
                dataset.Add(DicomTag.ScheduledProcedureStepStartDate, scheduledDateTime.ToString("yyyyMMdd"));
                dataset.Add(DicomTag.ScheduledProcedureStepStartTime, scheduledDateTime.ToString("HHmmss"));
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("WorklistSCP", "处理预约时间失败 - PatientId: {PatientId}, DateTime: {DateTime}, Error: {Error}", 
                    item.PatientId, item.ScheduledDateTime, ex.Message);
                // 使用默认值或跳过
                dataset.Add(DicomTag.ScheduledProcedureStepStartDate, "19000101");
                dataset.Add(DicomTag.ScheduledProcedureStepStartTime, "000000");
            }

            dataset.Add(DicomTag.ScheduledStationName, item.ScheduledStationName);
            dataset.Add(DicomTag.ScheduledProcedureStepID, item.ScheduledProcedureStepID);
            dataset.Add(DicomTag.RequestedProcedureID, item.RequestedProcedureID);

            var response = new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = dataset };
            DicomLogger.Debug("WorklistSCP", "成功创建响应 - PatientId: {PatientId}, AccessionNumber: {AccessionNumber}", 
                item.PatientId, item.AccessionNumber);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "创建响应数据集失败 - PatientId: {PatientId}", item.PatientId);
            throw new DicomNetworkException("Failed to create worklist response", ex);
        }
    }

    private WorklistQueryParameters ExtractQueryParameters(DicomCFindRequest request)
    {
        // 记录原始请求参数
        DicomLogger.Debug("WorklistSCP", "接收到查询请求: {@Tags}", 
            request.Dataset.Where(x => !x.Tag.IsPrivate)
                         .ToDictionary(x => x.Tag.ToString(), x => x.ToString()));

        var modality = GetModality(request.Dataset);
        var dateRange = GetDateRange(request.Dataset);
        
        var parameters = new WorklistQueryParameters(
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty),
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty),
            dateRange,
            modality,
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.ScheduledStationName, string.Empty)
        );

        DicomLogger.Information("WorklistSCP", "解析后的查询参数 - PatientId: {PatientId}, AccessionNumber: {AccessionNumber}, " +
            "日期范围: {StartDate} - {EndDate}, Modality: {Modality}, StationName: {StationName}",
            parameters.PatientId,
            parameters.AccessionNumber,
            parameters.DateRange.StartDate,
            parameters.DateRange.EndDate,
            parameters.Modality,
            parameters.ScheduledStationName);

        return parameters;
    }

    private string GetModality(DicomDataset dataset)
    {
        // 首先尝试从 ScheduledProcedureStep Sequence 获取
        var modality = string.Empty;
        if (dataset.Contains(DicomTag.ScheduledProcedureStepSequence))
        {
            var stepSequence = dataset.GetSequence(DicomTag.ScheduledProcedureStepSequence);
            if (stepSequence.Items.Any())
            {
                modality = stepSequence.Items[0].GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
                if (!string.IsNullOrEmpty(modality))
                {
                    DicomLogger.Debug("WorklistSCP", "从 ScheduledProcedureStep 获取到 Modality: {Modality}", modality);
                    return modality;
                }
            }
        }

        // 如果没有找到，试从根级别获取
        modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
        DicomLogger.Debug("WorklistSCP", "从根级别获取到 Modality: {Modality}", modality);
        return modality;
    }

    private (string StartDate, string EndDate) GetDateRange(DicomDataset dataset)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        string startDate, endDate;

        if (dataset.Contains(DicomTag.ScheduledProcedureStepStartDate))
        {
            var dateElement = dataset.GetDicomItem<DicomElement>(DicomTag.ScheduledProcedureStepStartDate);
            var values = dateElement?.Get<string[]>() ?? Array.Empty<string>();

            if (values.Length > 0)
            {
                var validDates = values
                    .Where(v => !string.IsNullOrEmpty(v) && v.Length == 8)
                    .ToList();

                if (validDates.Any())
                {
                    startDate = validDates.Min() ?? today;
                    endDate = today;
                    var validDatesStr = string.Join(",", validDates);
                    DicomLogger.Debug("WorklistSCP", "日期处理: 有效日期={ValidDates}, 选择的日期范围: {StartDate} - {EndDate}", 
                        validDatesStr, startDate, endDate);
                }
                else
                {
                    // 如果传了日期但无效，使用今天的日期范围
                    startDate = today;
                    endDate = today;
                    DicomLogger.Debug("WorklistSCP", "日期处理: 无效日期, 使用今天: {Today}", today);
                }
            }
            else
            {
                // 传了空的日期值，使用今天的日期范围
                startDate = today;
                endDate = today;
                DicomLogger.Debug("WorklistSCP", "日期处理: 空日期值, 使用今天: {Today}", today);
            }
        }
        else
        {
            // 没有传日期参数，使用过去30天到未来30天的范围
            startDate = DateTime.Now.AddDays(-30).ToString("yyyyMMdd");
            endDate = DateTime.Now.AddDays(30).ToString("yyyyMMdd");
            DicomLogger.Debug("WorklistSCP", "日期处理: 未传日期, 使用默认范围: {StartDate} - {EndDate}", 
                startDate, endDate);
        }

        var dateRange = (StartDate: startDate, EndDate: endDate);
        DicomLogger.Information("WorklistSCP", "最终查询日期范围: {StartDate} - {EndDate}", 
            dateRange.StartDate, dateRange.EndDate);
        return dateRange;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }
} 