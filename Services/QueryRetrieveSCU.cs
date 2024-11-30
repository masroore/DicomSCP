using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Models;

namespace DicomSCP.Services;

public interface IQueryRetrieveSCU
{
    Task<IEnumerable<DicomDataset>> QueryStudyAsync(DicomNodeConfig node, DicomDataset query);
    Task<IEnumerable<DicomDataset>> QuerySeriesAsync(DicomNodeConfig node, DicomDataset query);
    Task<IEnumerable<DicomDataset>> QueryImageAsync(DicomNodeConfig node, DicomDataset query);
    Task<bool> MoveStudyAsync(DicomNodeConfig node, string studyInstanceUid, string destinationAe);
}

public class QueryRetrieveSCU : IQueryRetrieveSCU
{
    private readonly DicomSettings _settings;
    private readonly QueryRetrieveConfig _config;

    public QueryRetrieveSCU(IOptions<DicomSettings> settings, IOptions<QueryRetrieveConfig> config)
    {
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<IEnumerable<DicomDataset>> QueryStudyAsync(DicomNodeConfig node, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        
        DicomLogger.Information("开始执行Study级别C-FIND查询 - AET: {AeTitle}, Host: {Host}:{Port}", 
            node.AeTitle, node.HostName, node.Port);
        
        var client = CreateClient(node);
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);
        
        // 添加查询数据集
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                results.Add(response.Dataset);
                DicomLogger.Debug("收到Study查询结果 - PatientId: {PatientId}, StudyInstanceUid: {StudyUid}",
                    response.Dataset.GetString(DicomTag.PatientID),
                    response.Dataset.GetString(DicomTag.StudyInstanceUID));
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
            
            DicomLogger.Information("Study查询完成 - 共找到 {Count} 条结果", results.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error(ex, "执行C-FIND查询时发生错误 - AET: {AeTitle}", node.AeTitle);
            throw;
        }

        return results;
    }

    public async Task<IEnumerable<DicomDataset>> QuerySeriesAsync(DicomNodeConfig node, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        
        DicomLogger.Information("开始执行Series级别C-FIND查询 - AET: {AeTitle}, StudyInstanceUid: {StudyUid}", 
            node.AeTitle, query.GetString(DicomTag.StudyInstanceUID));
        
        var client = CreateClient(node);
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series);
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                results.Add(response.Dataset);
                DicomLogger.Debug("收到Series查询结果 - SeriesInstanceUid: {SeriesUid}, Modality: {Modality}",
                    response.Dataset.GetString(DicomTag.SeriesInstanceUID),
                    response.Dataset.GetString(DicomTag.Modality));
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
            
            DicomLogger.Information("Series查询完成 - 共找到 {Count} 条结果", results.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error(ex, "执行C-FIND Series查询时发生错误 - AET: {AeTitle}", node.AeTitle);
            throw;
        }

        return results;
    }

    public async Task<IEnumerable<DicomDataset>> QueryImageAsync(DicomNodeConfig node, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        
        DicomLogger.Information("开始执行Image级别C-FIND查询 - AET: {AeTitle}, SeriesInstanceUid: {SeriesUid}", 
            node.AeTitle, query.GetString(DicomTag.SeriesInstanceUID));
        
        var client = CreateClient(node);
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Image);
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                results.Add(response.Dataset);
                DicomLogger.Debug("收到Image查询结果 - SopInstanceUid: {SopInstanceUid}, InstanceNumber: {Number}",
                    response.Dataset.GetString(DicomTag.SOPInstanceUID),
                    response.Dataset.GetString(DicomTag.InstanceNumber));
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
            
            DicomLogger.Information("Image查询完成 - 共找到 {Count} 条结果", results.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error(ex, "执行C-FIND Image查询时发生错误 - AET: {AeTitle}", node.AeTitle);
            throw;
        }

        return results;
    }

    public async Task<bool> MoveStudyAsync(DicomNodeConfig node, string studyInstanceUid, string destinationAe)
    {
        DicomLogger.Information("开始执行C-MOVE - 源AET: {SourceAet}, 目标AET: {DestinationAe}, StudyInstanceUid: {StudyUid}", 
            node.AeTitle, destinationAe, studyInstanceUid);
            
        var client = CreateClient(node);
        var request = new DicomCMoveRequest(destinationAe, studyInstanceUid);

        var success = true;
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Success)
            {
                DicomLogger.Information("C-MOVE成功完成 - StudyInstanceUid: {StudyUid}", studyInstanceUid);
            }
            else if (response.Status == DicomStatus.Pending)
            {
                DicomLogger.Information("C-MOVE进行中 - Status: {Status}", response.Status);
            }
            else
            {
                DicomLogger.Warning("C-MOVE失败 - Status: {Status}, StudyInstanceUid: {StudyUid}", 
                    response.Status, studyInstanceUid);
                success = false;
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
            return success;
        }
        catch (Exception ex)
        {
            DicomLogger.Error(ex, "执行C-MOVE时发生错误 - StudyInstanceUid: {StudyUid}", studyInstanceUid);
            throw;
        }
    }

    private IDicomClient CreateClient(DicomNodeConfig node)
    {
        DicomLogger.Debug("创建DICOM客户端连接 - LocalAE: {LocalAE}, RemoteAE: {RemoteAE}, Host: {Host}:{Port}",
            _config.LocalAeTitle, node.AeTitle, node.HostName, node.Port);
            
        var client = DicomClientFactory.Create(node.HostName, node.Port, false, _config.LocalAeTitle, node.AeTitle);
        client.NegotiateAsyncOps();
        return client;
    }
} 