using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Services;
using System.IO;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DicomController : ControllerBase
{
    private readonly DicomServer _server;
    private readonly DicomSettings _settings;
    private readonly ILogger<DicomController> _logger;

    public DicomController(
        DicomServer server,
        IOptions<DicomSettings> settings,
        ILogger<DicomController> logger)
    {
        _server = server;
        _settings = settings.Value;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        _settings.AeTitle,
        _settings.Port,
        _settings.StoragePath,
        IsRunning = _server.IsRunning
    });

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        try
        {
            if (_server.IsRunning)
                return BadRequest("服务器已经在运行");

            await _server.StartAsync();
            return Ok("服务器已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动服务器失败");
            return StatusCode(500, "启动服务器失败");
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        try
        {
            if (!_server.IsRunning)
                return BadRequest("服务器未运行");

            await _server.StopAsync();
            return Ok("服务器已停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止服务器失败");
            return StatusCode(500, "停止服务器失败");
        }
    }

    [HttpPost("settings")]
    public IActionResult UpdateSettings([FromBody] DicomSettingsUpdate settings)
    {
        if (_server.IsRunning)
            return BadRequest("无法在服务器运行时更新配置");

        if (!string.IsNullOrEmpty(settings.StoragePath))
        {
            try
            {
                var path = Path.GetFullPath(settings.StoragePath);
                Directory.CreateDirectory(path);
                CStoreSCP.Configure(path);
                return Ok("配置已更新");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新配置失败");
                return BadRequest($"存储路径无效: {ex.Message}");
            }
        }

        return Ok("无需更新");
    }
}

public record DicomSettingsUpdate(string? StoragePath, string? AeTitle); 