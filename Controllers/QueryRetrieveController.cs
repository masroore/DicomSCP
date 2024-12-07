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
    private readonly DicomSettings _settings;

    public QueryRetrieveController(
        IQueryRetrieveSCU queryRetrieveScu,
        IOptions<QueryRetrieveConfig> config,
        IOptions<DicomSettings> settings)
    {
        _queryRetrieveScu = queryRetrieveScu;
        _config = config.Value;
        _settings = settings.Value;
    }

    [HttpGet("nodes")]
    public ActionResult<IEnumerable<RemoteNode>> GetNodes()
    {
        return Ok(_config.RemoteNodes);
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

            // 处理查询参数
            if (queryParams != null)
            {
                // 患者ID
                if (queryParams.TryGetValue("patientId", out var patientId) && !string.IsNullOrWhiteSpace(patientId))
                {
                    dataset.AddOrUpdate(DicomTag.PatientID, patientId);
                }

                // 患者姓名（添加模糊匹配）
                if (queryParams.TryGetValue("patientName", out var patientName) && !string.IsNullOrWhiteSpace(patientName))
                {
                    dataset.AddOrUpdate(DicomTag.PatientName, $"*{patientName}*");
                }

                // 检查号
                if (queryParams.TryGetValue("accessionNumber", out var accessionNumber) && !string.IsNullOrWhiteSpace(accessionNumber))
                {
                    dataset.AddOrUpdate(DicomTag.AccessionNumber, accessionNumber);
                }

                // 检查类型
                if (queryParams.TryGetValue("modality", out var modality) && !string.IsNullOrWhiteSpace(modality))
                {
                    dataset.AddOrUpdate(DicomTag.ModalitiesInStudy, modality);
                }

                // 检查日期
                if (queryParams.TryGetValue("studyDate", out var studyDate) && !string.IsNullOrWhiteSpace(studyDate))
                {
                    // 转换日期格式为DICOM格式 (YYYYMMDD)
                    if (DateTime.TryParse(studyDate, out var date))
                    {
                        dataset.AddOrUpdate(DicomTag.StudyDate, date.ToString("yyyyMMdd"));
                    }
                }
            }

            DicomLogger.Information("Api", "执行Study查询 - Node: {Node}, Params: {Params}", 
                nodeId, 
                queryParams ?? new Dictionary<string, string>());

            var results = await _queryRetrieveScu.QueryStudyAsync(node, dataset);
            var studyResults = results.Select(DicomStudyResult.FromDataset).ToList();
            
            DicomLogger.Information("Api", "Study查询完成 - 找到 {Count} 条结果", studyResults.Count);
            
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
    public async Task<ActionResult> MoveStudy(string nodeId, string studyUid)
    {
        var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == nodeId);
        if (node == null)
        {
            return NotFound($"未找到节点: {nodeId}");
        }

        try
        {
            // 使用配置的 AE Title
            var destinationAe = _settings.AeTitle;

            // 记录开始获取的日志
            DicomLogger.Information("QueryRetrieveSCU", 
                "开始获取影像 - 源节点: {SourceAet}@{Host}:{Port}, 目标节点: {DestAet}, StudyInstanceUid: {StudyUid}", 
                node.AeTitle, node.HostName, node.Port, destinationAe, studyUid);

            // 尝试发起C-MOVE请求
            var moveTask = Task.Run(async () =>
            {
                try
                {
                    var success = await _queryRetrieveScu.MoveStudyAsync(node, studyUid, destinationAe);
                    return success;
                }
                catch
                {
                    return false;
                }
            });

            // 等待一小段时间，看看是否能快速确认请求是否被接受
            if (await Task.WhenAny(moveTask, Task.Delay(2000)) == moveTask)
            {
                // 如果在2秒内得���结果
                var success = await moveTask;
                if (!success)
                {
                    return StatusCode(500, new { message = "影像获取请求被拒绝" });
                }
            }

            // 请求已被接受，返回成功
            return Ok(new { 
                message = "影像获取请求已发送，请稍后在影像管理中查看",
                studyUid = studyUid,
                sourceAet = node.AeTitle,
                destinationAe = destinationAe
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex, 
                "发送获取请求失败 - StudyInstanceUid: {StudyUid}", studyUid);
            return StatusCode(500, new { message = "发送获取请求失败" });
        }
    }
}

// 添加用于转换查询结果的类
public class DicomStudyResult
{
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public DateTime? StudyDate { get; set; }
    public string StudyDescription { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public int NumberOfSeries { get; set; }
    public int NumberOfInstances { get; set; }

    public static DicomStudyResult FromDataset(DicomDataset dataset)
    {
        DateTime? studyDate = null;
        try
        {
            var dateStr = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty);
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length == 8)
            {
                var year = int.Parse(dateStr.Substring(0, 4));
                var month = int.Parse(dateStr.Substring(4, 2));
                var day = int.Parse(dateStr.Substring(6, 2));
                studyDate = new DateTime(year, month, day);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Warning("Api", ex, "解析研究日期失败");
        }

        return new DicomStudyResult
        {
            PatientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            PatientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
            AccessionNumber = dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
            Modality = dataset.GetSingleValueOrDefault(DicomTag.ModalitiesInStudy, string.Empty),
            StudyDate = studyDate,
            StudyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
            StudyInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
            NumberOfSeries = dataset.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedSeries, 0),
            NumberOfInstances = dataset.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedInstances, 0)
        };
    }
}