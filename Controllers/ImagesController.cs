using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Data;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly DicomRepository _repository;
    private readonly ILogger<ImagesController> _logger;
    private readonly IConfiguration _configuration;

    public ImagesController(DicomRepository repository, ILogger<ImagesController> logger, IConfiguration configuration)
    {
        _repository = repository;
        _logger = logger;
        _configuration = configuration;
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

    [HttpDelete("{studyInstanceUid}")]
    public async Task<IActionResult> Delete(string studyInstanceUid)
    {
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            return BadRequest("StudyInstanceUID is required");
        }

        try
        {
            // 1. 从数据库删除记录
            var study = await _repository.GetStudyAsync(studyInstanceUid);
            if (study == null)
            {
                return NotFound("检查不存在");
            }

            await _repository.DeleteStudyAsync(studyInstanceUid);

            // 2. 删除文件系统中的文件
            var storagePath = _configuration["DicomSettings:StoragePath"] 
                ?? throw new InvalidOperationException("DicomSettings:StoragePath is not configured");
            var studyPath = Path.Combine(storagePath, studyInstanceUid);
            if (Directory.Exists(studyPath))
            {
                Directory.Delete(studyPath, true);
            }

            return Ok(new { message = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除检查失败 - 检查实例UID: {StudyInstanceUid}", studyInstanceUid);
            return StatusCode(500, new { error = "删除失败，请重试" });
        }
    }
} 