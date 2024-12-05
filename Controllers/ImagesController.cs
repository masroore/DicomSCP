using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Data;
using Microsoft.Extensions.Configuration;
using DicomSCP.Services;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly DicomRepository _repository;
    private readonly IConfiguration _configuration;

    public ImagesController(DicomRepository repository, IConfiguration configuration)
    {
        _repository = repository;
        _configuration = configuration;

        // 验证必要的配置是否存在
        var storagePath = configuration["DicomSettings:StoragePath"];
        if (string.IsNullOrEmpty(storagePath))
        {
            throw new InvalidOperationException("DicomSettings:StoragePath must be configured in appsettings.json");
        }
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<StudyInfo>>> GetStudies(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? patientId = null,
        [FromQuery] string? patientName = null,
        [FromQuery] string? accessionNumber = null,
        [FromQuery] string? modality = null,
        [FromQuery] string? studyDate = null)
    {
        try
        {
            // 解析日期
            DateTime? searchDate = null;
            if (!string.IsNullOrEmpty(studyDate))
            {
                if (DateTime.TryParse(studyDate, out DateTime date))
                {
                    searchDate = date;
                }
            }

            var result = await _repository.GetStudiesAsync(
                page, 
                pageSize, 
                patientId, 
                patientName, 
                accessionNumber, 
                modality, 
                searchDate,    // 开始时间
                searchDate?.AddDays(1).AddSeconds(-1)  // 结束时间设为当天最后一秒
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取影像列表失败");
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
            DicomLogger.Error("Api", ex, "[API] 获取序列列表失败 - StudyUid: {StudyUid}", studyUid);
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
            DicomLogger.Error("Api", ex, "[API] 删除检查失败 - 检查实例UID: {StudyInstanceUid}", studyInstanceUid);
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
            DicomLogger.Error("Api", ex, "[API] 获取序列实例失败");
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
                DicomLogger.Warning("Api", "[API] 实例不存在 - InstanceUid: {InstanceUid}", instanceUid);
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

            DicomLogger.Information("Api",
                "[API] 准备下载文件 - InstanceUid: {InstanceUid}, StoragePath: {StoragePath}, FullPath: {FullPath}", 
                instanceUid,
                storagePath,
                fullPath
            );

            if (!System.IO.File.Exists(fullPath))
            {
                DicomLogger.Error("Api", null,
                    "[API] 文件不存在 - InstanceUid: {InstanceUid}, StoragePath: {StoragePath}, FullPath: {FullPath}", 
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
            DicomLogger.Error("Api", ex, "[API] 下载文件失败 - InstanceUid: {InstanceUid}", instanceUid);
            return StatusCode(500, "下载文件失败");
        }
    }
} 