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
    Task<IEnumerable<DicomDataset>> QueryStudyAsync(RemoteNode node, DicomDataset query);
    Task<IEnumerable<DicomDataset>> QuerySeriesAsync(RemoteNode node, DicomDataset query);
    Task<IEnumerable<DicomDataset>> QueryImageAsync(RemoteNode node, DicomDataset query);
    Task<bool> MoveStudyAsync(RemoteNode node, string studyInstanceUid, string destinationAe);
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

    public async Task<bool> MoveStudyAsync(RemoteNode node, string studyInstanceUid, string destinationAe)
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
                "开始执行C-MOVE - 源AET: {SourceAet}, 目标AET: {DestinationAe}, StudyInstanceUid: {StudyUid}", 
                node.AeTitle, destinationAe, studyInstanceUid);
                
            var request = new DicomCMoveRequest(destinationAe, studyInstanceUid);
            var success = true;

            request.OnResponseReceived += (req, response) =>
            {
                if (response.Status == DicomStatus.Success)
                {
                    DicomLogger.Information("QueryRetrieveSCU",
                        "C-MOVE完成 - StudyInstanceUid: {StudyUid}", 
                        studyInstanceUid);
                }
                else if (response.Status == DicomStatus.Pending && response.HasDataset)
                {
                    // 只记录进度，不影响成功状态
                    var remaining = response.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfRemainingSuboperations, 0);
                    var completed = response.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfCompletedSuboperations, 0);
                    var failed = response.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfFailedSuboperations, 0);
                    
                    DicomLogger.Debug("QueryRetrieveSCU",
                        "C-MOVE进度 - 已完成: {Completed}, 失败: {Failed}, 剩余: {Remaining}, StudyInstanceUid: {StudyUid}",
                        completed, failed, remaining, studyInstanceUid);
                }
                else if (response.Status.State == DicomState.Failure)
                {
                    // 只有明确的失败状态才标记为失败
                    success = false;
                    DicomLogger.Warning("QueryRetrieveSCU", 
                        "C-MOVE失败 - Status: {Status}, StudyInstanceUid: {StudyUid}", 
                        response.Status, studyInstanceUid);
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync();

            return success;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex, 
                "C-MOVE操作失败 - StudyInstanceUid: {StudyUid}", 
                studyInstanceUid);
            throw;
        }
    }

    public async Task<IEnumerable<DicomDataset>> QueryStudyAsync(RemoteNode node, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        var client = CreateClient(node);
        
        DicomLogger.Information("QueryRetrieveSCU", "开始执行Study级别C-FIND查询 - AET: {AeTitle}, Host: {Host}:{Port}", 
            node.AeTitle, node.HostName, node.Port);
        
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.HasDataset)
            {
                results.Add(response.Dataset);
                DicomLogger.Debug("QueryRetrieveSCU", "收到Study查询结果 - PatientId: {PatientId}, StudyInstanceUid: {StudyUid}",
                    response.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, "(no id)"),
                    response.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(no uid)"));
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
            
            DicomLogger.Information("QueryRetrieveSCU", "Study查询完成 - 共找到 {Count} 条结果", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex, "执行Study查询时发生错误");
            throw;
        }
    }

    public async Task<IEnumerable<DicomDataset>> QuerySeriesAsync(RemoteNode node, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        var client = CreateClient(node);
        
        DicomLogger.Information("QueryRetrieveSCU", "开始执行Series级别C-FIND查询 - AET: {AeTitle}, StudyInstanceUid: {StudyUid}", 
            node.AeTitle, query.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(no uid)"));
        
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series);
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.HasDataset)
            {
                results.Add(response.Dataset);
                DicomLogger.Debug("QueryRetrieveSCU", "收到Series查询结果 - SeriesInstanceUid: {SeriesUid}, Modality: {Modality}",
                    response.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "(no uid)"),
                    response.Dataset.GetSingleValueOrDefault(DicomTag.Modality, "(no modality)"));
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
            return results;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex, "执行Series查询时发生错误");
            throw;
        }
    }

    public async Task<IEnumerable<DicomDataset>> QueryImageAsync(RemoteNode node, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        var client = CreateClient(node);
        
        DicomLogger.Information("QueryRetrieveSCU", "开始执行Image级别C-FIND查询 - AET: {AeTitle}, SeriesInstanceUid: {SeriesUid}", 
            node.AeTitle, query.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "(no uid)"));
        
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Image);
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.HasDataset)
            {
                results.Add(response.Dataset);
                DicomLogger.Debug("QueryRetrieveSCU", "收到Image查询结果 - SopInstanceUid: {SopInstanceUid}, InstanceNumber: {Number}",
                    response.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "(no uid)"),
                    response.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, "(no number)"));
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
            return results;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex, "执行Image查询时发生错误");
            throw;
        }
    }
} 