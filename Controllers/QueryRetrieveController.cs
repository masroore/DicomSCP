using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using FellowOakDicom;
using DicomSCP.Services;
using DicomSCP.Configuration;
using DicomSCP.Models;
using System.Text.Json;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QueryRetrieveController : ControllerBase
{
    private readonly IQueryRetrieveSCU _queryRetrieveScu;
    private readonly QueryRetrieveConfig _config;

    public QueryRetrieveController(
        IQueryRetrieveSCU queryRetrieveScu,
        IOptions<QueryRetrieveConfig> config)
    {
        _queryRetrieveScu = queryRetrieveScu;
        _config = config.Value;
    }

    [HttpGet("nodes")]
    public ActionResult<IEnumerable<RemoteNode>> GetNodes()
    {
        return Ok(_config.RemoteNodes);
    }

    [HttpGet("nodes/default")]
    public ActionResult<RemoteNode> GetDefaultNode()
    {
        var node = _config.RemoteNodes?.FirstOrDefault(n => n.IsDefault);
        if (node == null)
        {
            return NotFound("未配置默认节点");
        }
        return Ok(node);
    }

    [HttpPost("{nodeId}/query/study")]
    public async Task<ActionResult<IEnumerable<DicomStudyResult>>> QueryStudy(string nodeId, [FromBody] Dictionary<string, string> queryParams)
    {
        var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == nodeId);
        if (node == null)
        {
            return NotFound($"未找到节点: {nodeId}");
        }

        try
        {
            var dataset = new DicomDataset();
            dataset.Add(DicomTag.QueryRetrieveLevel, "STUDY");
            
            // 添加必要的返回字段
            dataset.Add(DicomTag.StudyInstanceUID, "");
            dataset.Add(DicomTag.StudyDate, "");
            dataset.Add(DicomTag.StudyTime, "");
            dataset.Add(DicomTag.PatientName, "");
            dataset.Add(DicomTag.PatientID, "");
            dataset.Add(DicomTag.StudyDescription, "");
            dataset.Add(DicomTag.ModalitiesInStudy, "");
            dataset.Add(DicomTag.NumberOfStudyRelatedSeries, "");
            dataset.Add(DicomTag.NumberOfStudyRelatedInstances, "");
            dataset.Add(DicomTag.AccessionNumber, "");

            // 添加查询参数
            foreach (var param in queryParams)
            {
                if (DicomDictionary.Default[param.Key] != null)
                {
                    dataset.Add(DicomDictionary.Default[param.Key], param.Value);
                }
            }

            var results = await _queryRetrieveScu.QueryStudyAsync(node, dataset);
            var studyResults = results.Select(DicomStudyResult.FromDataset).ToList();
            return Ok(studyResults);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "执行Study查询失败");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("{nodeId}/query/series/{studyUid}")]
    public async Task<ActionResult<IEnumerable<object>>> QuerySeries(string nodeId, string studyUid)
    {
        var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == nodeId);
        if (node == null)
        {
            return NotFound($"未找到节点: {nodeId}");
        }

        try
        {
            var dataset = new DicomDataset();
            dataset.Add(DicomTag.QueryRetrieveLevel, "SERIES");
            dataset.Add(DicomTag.StudyInstanceUID, studyUid);
            
            // 添加必要的返回字段
            dataset.Add(DicomTag.SeriesInstanceUID, "");
            dataset.Add(DicomTag.SeriesNumber, "");
            dataset.Add(DicomTag.SeriesDescription, "");
            dataset.Add(DicomTag.Modality, "");
            dataset.Add(DicomTag.NumberOfSeriesRelatedInstances, "");

            var results = await _queryRetrieveScu.QuerySeriesAsync(node, dataset);
            
            var series = results.Select(ds => new
            {
                SeriesInstanceUid = ds.GetString(DicomTag.SeriesInstanceUID),
                SeriesNumber = ds.GetSingleValueOrDefault(DicomTag.SeriesNumber, ""),
                SeriesDescription = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, ""),
                Modality = ds.GetString(DicomTag.Modality),
                InstanceCount = ds.GetSingleValueOrDefault(DicomTag.NumberOfSeriesRelatedInstances, "0")
            });

            return Ok(series);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 执行Series查询失败");
            return StatusCode(500, "查询失败: " + ex.Message);
        }
    }

    [HttpPost("{nodeId}/move/{studyUid}")]
    public async Task<ActionResult> MoveStudy(string nodeId, string studyUid, [FromBody] MoveRequest request)
    {
        var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == nodeId);
        if (node == null)
        {
            return NotFound($"未找到节点: {nodeId}");
        }

        try
        {
            var success = await _queryRetrieveScu.MoveStudyAsync(node, studyUid, request.DestinationAe);
            if (success)
            {
                return Ok(new { message = "检查传输已开始" });
            }
            else
            {
                return StatusCode(500, new { message = "检查传输失败" });
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 执行C-MOVE失败");
            return StatusCode(500, new { message = "检查传输失败: " + ex.Message });
        }
    }
}

public class MoveRequest
{
    public string DestinationAe { get; set; } = string.Empty;
}