using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Data;

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
} 