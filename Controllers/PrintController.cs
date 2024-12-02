using Microsoft.AspNetCore.Mvc;
using DicomSCP.Data;
using DicomSCP.Models;
using DicomSCP.Services;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/print")]
public class PrintController : ControllerBase
{
    private readonly DicomRepository _repository;

    public PrintController(DicomRepository repository)
    {
        _repository = repository;
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> GetJobs()
    {
        try
        {
            var jobs = await _repository.GetPrintJobsAsync();
            return Ok(jobs);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取打印作业列表失败");
            return StatusCode(500, "获取打印作业列表失败");
        }
    }

    [HttpPost("jobs/{jobId}/start")]
    public async Task<IActionResult> StartJob(string jobId)
    {
        try
        {
            var job = await _repository.GetPrintJobAsync(jobId);
            if (job == null)
            {
                return NotFound("打印作业不存在");
            }

            await _repository.UpdatePrintJobStatusAsync(jobId, "PRINTING");
            return Ok();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 启动打印作业失败 - JobId: {JobId}", jobId);
            return StatusCode(500, "启动打印作业失败");
        }
    }

    [HttpDelete("jobs/{jobId}")]
    public async Task<IActionResult> DeleteJob(string jobId)
    {
        try
        {
            var success = await _repository.DeletePrintJobAsync(jobId);
            if (!success)
            {
                return NotFound("打印作业不存在");
            }
            return NoContent();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 删除打印作业失败 - JobId: {JobId}", jobId);
            return StatusCode(500, "删除打印作业失败");
        }
    }
} 