using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;


namespace DicomSCP.Services;

public interface IQueryRetrieveSCU
{
    Task<IEnumerable<DicomDataset>> QueryAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset query);
    Task<bool> MoveAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset dataset, string destinationAe, string? transferSyntax = null);
    Task<bool> VerifyConnectionAsync(RemoteNode node);
}

public class QueryRetrieveSCU : IQueryRetrieveSCU
{
    private readonly QueryRetrieveConfig _config;
    private readonly DicomSettings _settings;

    public QueryRetrieveSCU(
        IOptions<QueryRetrieveConfig> config,
        IOptions<DicomSettings> settings)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
    }

    private IDicomClient CreateClient(RemoteNode node)
    {
        DicomLogger.Debug("QueryRetrieveSCU", "创建DICOM客户端连接 - LocalAE: {LocalAE}, RemoteAE: {RemoteAE}, Host: {Host}:{Port}",
            _config.LocalAeTitle, node.AeTitle, node.HostName, node.Port);
            
        return DicomClientFactory.Create(
            node.HostName, 
            node.Port, 
            false, 
            _config.LocalAeTitle, 
            node.AeTitle);
    }

    public async Task<bool> MoveAsync(
        RemoteNode node, 
        DicomQueryRetrieveLevel level, 
        DicomDataset dataset, 
        string destinationAe,
        string? transferSyntax = null)
    {
        try
        {
            // 验证目标 AE 是否是本地 StoreSCP
            if (destinationAe != _settings.AeTitle)
            {
                DicomLogger.Error("QueryRetrieveSCU", 
                    "目标AE不是本地StoreSCP - DestAE: {DestAE}, LocalAE: {LocalAE}", 
                    destinationAe, _settings.AeTitle);
                return false;
            }

            var client = CreateClient(node);
            
            DicomLogger.Information("QueryRetrieveSCU", 
                "开始执行{Level}级别C-MOVE - 源AET: {SourceAet}, 目标AET: {DestinationAe}, 传输语法: {TransferSyntax}", 
                level, node.AeTitle, destinationAe, transferSyntax ?? "默认");

            // 添加查询级别
            dataset.AddOrUpdate(DicomTag.QueryRetrieveLevel, level.ToString().ToUpper());

            // 获取 StudyInstanceUID
            var studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "");
            
            // 创建 C-MOVE 请求
            var request = new DicomCMoveRequest(destinationAe, studyUid);
            request.Dataset = dataset;

            // 如果指定了传输语法，则配置客户端的传输语法
            if (!string.IsNullOrEmpty(transferSyntax))
            {
                try 
                {
                    // 获取适当的 SOP Class UID
                    var sopClassUid = GetAppropriateSOPClassUID(level, dataset);
                    
                    // 配置客户端的传输语法
                    client.AdditionalPresentationContexts.Clear();
                    
                    // 创建传输语法数组
                    var transferSyntaxes = new[] { DicomTransferSyntax.Parse(transferSyntax) };
                    
                    // 创建表示上下文，包含所有必需的参数
                    var presentationContext = new DicomPresentationContext(
                        1,  // presentation context ID
                        sopClassUid,  // abstract syntax (SOP Class UID)
                        true,  // provider role (SCU)
                        false  // user role (not SCP)
                    );
                    
                    // 添加传输语法
                    foreach (var syntax in transferSyntaxes)
                    {
                        presentationContext.AddTransferSyntax(syntax);
                    }
                    
                    client.AdditionalPresentationContexts.Add(presentationContext);

                    DicomLogger.Debug("QueryRetrieveSCU", 
                        "已设置传输语法 - SOP Class: {SopClass}, TransferSyntax: {TransferSyntax}",
                        sopClassUid.Name, transferSyntax);
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("QueryRetrieveSCU", ex,
                        "设置传输语法失败，将使用默认传输语法 - TransferSyntax: {TransferSyntax}",
                        transferSyntax);
                }
            }

            var hasReceivedResponse = false;

            request.OnResponseReceived += (req, response) =>
            {
                hasReceivedResponse = true;

                if (response.Status == DicomStatus.Pending)
                {
                    DicomLogger.Debug("QueryRetrieveSCU", 
                        "{Level}级别C-MOVE正在传输 - TransferSyntax: {TransferSyntax}", 
                        level, transferSyntax ?? "默认");
                }
                else if (response.Status.State == DicomState.Failure)
                {
                    DicomLogger.Warning("QueryRetrieveSCU", 
                        "{Level}级别C-MOVE失败 - {Error}, TransferSyntax: {TransferSyntax}", 
                        level, response.Status.ErrorComment, transferSyntax ?? "默认");
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync();

            // 只要收到响应就返回成功
            return hasReceivedResponse;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex, 
                "{Level}级别C-MOVE失败 - TransferSyntax: {TransferSyntax}", 
                level, transferSyntax ?? "默认");
            throw;
        }
    }

    // 添加辅助方法来获取适当的 SOP Class UID
    private DicomUID GetAppropriateSOPClassUID(DicomQueryRetrieveLevel level, DicomDataset dataset)
    {
        // 首先尝试从数据集中获取 SOP Class UID
        var sopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
        if (!string.IsNullOrEmpty(sopClassUid))
        {
            return DicomUID.Parse(sopClassUid);
        }

        // 如果数据集中没有 SOP Class UID，则根据查询级别返回适当的存储 SOP Class
        switch (level)
        {
            case DicomQueryRetrieveLevel.Patient:
            case DicomQueryRetrieveLevel.Study:
                // 对于 Patient 和 Study 级别，使用通用的 Study Root Query/Retrieve Information Model
                return DicomUID.StudyRootQueryRetrieveInformationModelMove;

            case DicomQueryRetrieveLevel.Series:
            case DicomQueryRetrieveLevel.Image:
                // 尝试从数据集中获取模态信息
                var modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
                switch (modality.ToUpper())
                {
                    case "CT":
                        return DicomUID.CTImageStorage;
                    case "MR":
                        return DicomUID.MRImageStorage;
                    case "US":
                        return DicomUID.UltrasoundImageStorage;
                    case "XA":
                        return DicomUID.XRayAngiographicImageStorage;
                    case "CR":
                        return DicomUID.ComputedRadiographyImageStorage;
                    case "DX":
                        return DicomUID.DigitalXRayImageStorageForPresentation;
                    case "SC":
                        return DicomUID.SecondaryCaptureImageStorage;
                    default:
                        // 如果无法确定具体类型，使用通用的 Study Root Query/Retrieve Information Model
                        return DicomUID.StudyRootQueryRetrieveInformationModelMove;
                }

            default:
                return DicomUID.StudyRootQueryRetrieveInformationModelMove;
        }
    }

    public async Task<IEnumerable<DicomDataset>> QueryAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        var client = CreateClient(node);
        
        DicomLogger.Information("QueryRetrieveSCU", 
            "开始执行{Level}级别C-FIND查询 - AET: {AeTitle}, Host: {Host}:{Port}", 
            level, node.AeTitle, node.HostName, node.Port);

        var startTime = DateTime.Now;  // 添加计时

        var request = new DicomCFindRequest(level);
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.HasDataset)
            {
                results.Add(response.Dataset);
                // 记录每个响应的时间
                var elapsed = DateTime.Now - startTime;
                DicomLogger.Debug("QueryRetrieveSCU", 
                    "收到第{Count}个响应 - 耗时: {Elapsed}ms", 
                    results.Count, elapsed.TotalMilliseconds);
                LogQueryResult(level, response.Dataset);
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
            
            var totalTime = DateTime.Now - startTime;
            DicomLogger.Information("QueryRetrieveSCU", 
                "{Level}查询完成 - 共找到 {Count} 条结果, 总耗时: {Elapsed}ms", 
                level, results.Count, totalTime.TotalMilliseconds);

            return results;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex, 
                "执行{Level}查询时发生错误", level);
            throw;
        }
    }

    private void LogQueryResult(DicomQueryRetrieveLevel level, DicomDataset dataset)
    {
        switch (level)
        {
            case DicomQueryRetrieveLevel.Patient:
                DicomLogger.Debug("QueryRetrieveSCU", 
                    "收到Patient查询结果 - PatientId: {PatientId}, PatientName: {PatientName}",
                    dataset.GetSingleValueOrDefault(DicomTag.PatientID, "(no id)"),
                    dataset.GetSingleValueOrDefault(DicomTag.PatientName, "(no name)"));
                break;
            case DicomQueryRetrieveLevel.Study:
                DicomLogger.Debug("QueryRetrieveSCU", 
                    "收到Study查询结果 - PatientId: {PatientId}, StudyInstanceUid: {StudyUid}",
                    dataset.GetSingleValueOrDefault(DicomTag.PatientID, "(no id)"),
                    dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(no uid)"));
                break;
            case DicomQueryRetrieveLevel.Series:
                DicomLogger.Debug("QueryRetrieveSCU", 
                    "收到Series查询结果 - StudyInstanceUid: {StudyUid}, SeriesInstanceUid: {SeriesUid}",
                    dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(no study uid)"),
                    dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "(no series uid)"));
                break;
            case DicomQueryRetrieveLevel.Image:
                DicomLogger.Debug("QueryRetrieveSCU", 
                    "收到Image查询结果 - StudyInstanceUid: {StudyUid}, SeriesInstanceUid: {SeriesUid}, SopInstanceUid: {SopUid}",
                    dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(no study uid)"),
                    dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "(no series uid)"),
                    dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "(no sop uid)"));
                break;
        }
    }

    public async Task<bool> VerifyConnectionAsync(RemoteNode node)
    {
        try
        {
            var client = CreateClient(node);
            
            DicomLogger.Information("QueryRetrieveSCU", 
                "开始测试DICOM连接 - LocalAE: {LocalAE}, RemoteAE: {RemoteAE}, Host: {Host}:{Port}", 
                _config.LocalAeTitle, node.AeTitle, node.HostName, node.Port);

            // 创建 C-ECHO 请求
            var request = new DicomCEchoRequest();
            
            var success = true;
            request.OnResponseReceived += (req, response) => {
                if (response.Status.State != DicomState.Success)
                {
                    success = false;
                    DicomLogger.Warning("QueryRetrieveSCU", 
                        "C-ECHO失败 - Status: {Status}", response.Status);
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync();

            if (success)
            {
                DicomLogger.Information("QueryRetrieveSCU", 
                    "DICOM连接测试成功 - RemoteAE: {RemoteAE}", node.AeTitle);
            }

            return success;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex, 
                "DICOM连接测试失败 - RemoteAE: {RemoteAE}", node.AeTitle);
            return false;
        }
    }
} 