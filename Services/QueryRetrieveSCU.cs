using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Models;
using Serilog;
using ILogger = Serilog.ILogger;

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
    private static readonly ILogger _logger = Log.ForContext<QueryRetrieveSCU>();
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
        
        var client = CreateClient(node);
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);
        
        // 添加查询数据集
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                results.Add(response.Dataset);
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "执行C-FIND查询时发生错误");
            throw;
        }

        return results;
    }

    public async Task<IEnumerable<DicomDataset>> QuerySeriesAsync(DicomNodeConfig node, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        
        var client = CreateClient(node);
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series);
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                results.Add(response.Dataset);
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "执行C-FIND Series查询时发生错误");
            throw;
        }

        return results;
    }

    public async Task<IEnumerable<DicomDataset>> QueryImageAsync(DicomNodeConfig node, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        
        var client = CreateClient(node);
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Image);
        request.Dataset = query;
        
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                results.Add(response.Dataset);
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "执行C-FIND Image查询时发生错误");
            throw;
        }

        return results;
    }

    public async Task<bool> MoveStudyAsync(DicomNodeConfig node, string studyInstanceUid, string destinationAe)
    {
        var client = CreateClient(node);
        var request = new DicomCMoveRequest(destinationAe, studyInstanceUid);

        var success = true;
        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Success)
            {
                _logger.Information("C-MOVE成功完成");
            }
            else if (response.Status == DicomStatus.Pending)
            {
                _logger.Information("C-MOVE进行中: {Status}", response.Status);
            }
            else
            {
                _logger.Warning("C-MOVE失败: {Status}", response.Status);
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
            _logger.Error(ex, "执行C-MOVE时发生错误");
            throw;
        }
    }

    private IDicomClient CreateClient(DicomNodeConfig node)
    {
        var client = DicomClientFactory.Create(node.HostName, node.Port, false, _config.LocalAeTitle, node.AeTitle);
        client.NegotiateAsyncOps();
        return client;
    }
} 