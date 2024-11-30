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

        // 验证必要的配置是否存在
        var storagePath = configuration["DicomSettings:StoragePath"];
        if (string.IsNullOrEmpty(storagePath))
        {
            throw new InvalidOperationException("DicomSettings:StoragePath must be configured in appsettings.json");
        }
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
            _logger.LogError(ex, "获序列列表失败 - StudyUid: {StudyUid}", studyUid);
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

    [HttpGet("{studyUid}/series/{seriesUid}/instances")]
    public async Task<ActionResult<IEnumerable<object>>> GetSeriesInstances(string studyUid, string seriesUid)
    {
        try
        {
            var instances = await _repository.GetSeriesInstancesAsync(seriesUid);
            var result = instances.Select(instance => new
            {
                sopInstanceUid = instance.SopInstanceUid,
                instanceNumber = instance.InstanceNumber
            });
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取序列实例失败");
            return StatusCode(500, "获取序列实例失败");
        }
    }

    [HttpGet("download/{instanceUid}")]
    public async Task<IActionResult> DownloadInstance(string instanceUid)
    {
        try
        {
            var instance = await _repository.GetInstanceAsync(instanceUid);
            if (instance == null)
            {
                _logger.LogWarning("实例不存在 - InstanceUid: {InstanceUid}", instanceUid);
                return NotFound("实例不存在");
            }

            // 从配置获取存储根路径
            var storagePath = _configuration["DicomSettings:StoragePath"] 
                ?? throw new InvalidOperationException("DicomSettings:StoragePath is not configured");
            
            // 处理存储路径
            if (storagePath.StartsWith("./") || storagePath.StartsWith(".\\"))
            {
                // 如果是以./开头的相对路径，转换为基于应用程序根目录的绝对路径
                storagePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, storagePath.Substring(2)));
            }
            else if (!Path.IsPathRooted(storagePath))
            {
                // 其他相对路径，转换为基于应用程序根目录的绝对路径
                storagePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, storagePath));
            }

            // 拼接完整的文件路径并规范化
            var fullPath = Path.GetFullPath(Path.Combine(storagePath, instance.FilePath));

            _logger.LogInformation(
                "准备下载文件 - InstanceUid: {InstanceUid}, StoragePath: {StoragePath}, FullPath: {FullPath}", 
                instanceUid,
                storagePath,
                fullPath
            );

            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogError(
                    "文件不存在 - InstanceUid: {InstanceUid}, StoragePath: {StoragePath}, FullPath: {FullPath}", 
                    instanceUid,
                    storagePath,
                    fullPath
                );
                return NotFound("图像文件不存在");
            }

            // 构造文件名，使用 SOP Instance UID
            var fileName = $"{instance.SopInstanceUid}.dcm";

            // 使用 Append 而不是 Add 来设置 Content-Disposition 头
            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            
            return PhysicalFile(fullPath, "application/dicom");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载文件失败 - InstanceUid: {InstanceUid}", instanceUid);
            return StatusCode(500, "下载文件失败");
        }
    }
} 