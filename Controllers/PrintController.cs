using Microsoft.AspNetCore.Mvc;
using DicomSCP.Data;
using DicomSCP.Models;
using FellowOakDicom;
using FellowOakDicom.Imaging;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrintController : Controller
{
    private readonly DicomRepository _repository;
    private readonly ILogger<PrintController> _logger;

    public PrintController(DicomRepository repository, ILogger<PrintController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetPrintJobs()
    {
        try
        {
            var items = await _repository.GetPrintJobsAsync();
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取打印任务时发生错误");
            return StatusCode(500, new { message = "获取打印任务失败" });
        }
    }

    [HttpGet("{jobId}")]
    public async Task<IActionResult> GetPrintJob(string jobId)
    {
        try
        {
            var job = await _repository.GetPrintJobAsync(jobId);
            if (job == null)
            {
                return NotFound(new { message = "打印任务不存在" });
            }
            return Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取打印任务详情时发生错误");
            return StatusCode(500, new { message = "获取打印任务详情失败" });
        }
    }

    [HttpDelete("{jobId}")]
    public async Task<IActionResult> DeletePrintJob(string jobId)
    {
        try
        {
            var result = await _repository.DeletePrintJobAsync(jobId);
            if (!result)
            {
                return NotFound(new { message = "打印任务不存在" });
            }
            return Ok(new { message = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除��印任务时发生错误");
            return StatusCode(500, new { message = "删除打印任务失败" });
        }
    }
} 