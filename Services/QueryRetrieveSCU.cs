using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using Microsoft.Extensions.Logging;

namespace DicomSCP.Services;

public interface IQueryRetrieveSCU
{
    Task<IEnumerable<DicomDataset>> QueryAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset query);
    Task<bool> MoveAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset dataset, string destinationAe);
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

    public async Task<bool> MoveAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset dataset, string destinationAe)
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
                "开始执行{Level}级别C-MOVE - 源AET: {SourceAet}, 目标AET: {DestinationAe}", 
                level, node.AeTitle, destinationAe);

            // 添加查询级别
            dataset.AddOrUpdate(DicomTag.QueryRetrieveLevel, level.ToString().ToUpper());

            // 获取 StudyInstanceUID 作为 presentationContextId
            var studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "");
            
            // 使用正确的构造函数
            var request = new DicomCMoveRequest(destinationAe, studyUid);
            request.Dataset = dataset;  // 设置完整的数据集

            var hasReceivedResponse = false;

            request.OnResponseReceived += (req, response) =>
            {
                hasReceivedResponse = true;

                if (response.Status == DicomStatus.Pending)
                {
                    DicomLogger.Debug("QueryRetrieveSCU", 
                        "{Level}级别C-MOVE正在传输", level);
                }
                else if (response.Status.State == DicomState.Failure)
                {
                    DicomLogger.Warning("QueryRetrieveSCU", 
                        "{Level}级别C-MOVE失败 - {Error}", 
                        level, response.Status.ErrorComment);
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
                "{Level}级别C-MOVE失败", level);
            throw;
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