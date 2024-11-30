using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Data;
using FellowOakDicom;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly DicomRepository _repository;
    private readonly ILogger<ImagesController> _logger;

    public ImagesController(DicomRepository repository, ILogger<ImagesController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StudyInfo>>> GetAll()
    {
        try
        {
            // 从数据库中获取所有研究信息
            var studies = await _repository.GetAllStudiesWithPatientInfoAsync();
            var result = studies.Select(study => new StudyInfo
            {
                StudyInstanceUid = study.StudyInstanceUid,
                PatientId = study.PatientId,
                PatientName = study.PatientName,
                PatientSex = study.PatientSex,
                PatientBirthDate = study.PatientBirthDate,
                AccessionNumber = study.AccessionNumber,
                Modality = study.Modality,
                StudyDate = study.StudyDate,
                StudyDescription = study.StudyDescription
            });

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取影像列表失败");
            return StatusCode(500, "获取数据失败");
        }
    }

    [HttpGet("{studyUid}/series")]
    public async Task<ActionResult<IEnumerable<SeriesInfo>>> GetSeries(string studyUid)
    {
        try
        {
            var seriesList = await _repository.GetSeriesByStudyUidAsync(studyUid);
            var result = seriesList.Select(series => new SeriesInfo
            {
                SeriesInstanceUid = series.SeriesInstanceUid,
                SeriesNumber = series.SeriesNumber ?? "",
                Modality = series.StudyModality ?? series.Modality ?? "",
                SeriesDescription = series.SeriesDescription ?? "",
                NumberOfInstances = series.NumberOfInstances
            });

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取序列列表失败 - StudyUid: {StudyUid}", studyUid);
            return StatusCode(500, "获取数据失败");
        }
    }

    // 新增：获取列下的所有实例
    [HttpGet("series/{seriesUid}/instances")]
    public async Task<IActionResult> GetInstancesBySeriesUid(string seriesUid)
    {
        try
        {
            var instances = await _repository.GetInstancesBySeriesUidAsync(seriesUid);
            return Ok(instances);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取实例列表失败 - SeriesUid: {SeriesUid}", seriesUid);
            return StatusCode(500, "获取数据失败");
        }
    }

    // 新增：获取单个实例的DICOM数据
    [HttpGet("instances/{instanceUid}")]
    public async Task<IActionResult> GetInstance(string instanceUid)
    {
        try
        {
            var instance = await _repository.GetInstanceAsync(instanceUid);
            if (instance == null)
            {
                return NotFound(new { error = "Instance not found" });
            }

            if (!System.IO.File.Exists(instance.FilePath))
            {
                return NotFound(new { error = "DICOM file not found" });
            }

            // 返回文件字节流
            var bytes = await System.IO.File.ReadAllBytesAsync(instance.FilePath);
            
            // 设置响应头
            Response.Headers["Content-Disposition"] = "attachment; filename=" + instanceUid + ".dcm";
            
            // 返回原始字节流
            return File(bytes, "application/octet-stream");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取实例数据失败 - InstanceUid: {InstanceUid}", instanceUid);
            return StatusCode(500, new { error = ex.Message });
        }
    }
} 