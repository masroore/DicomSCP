using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// 获取DICOM服务器状态
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            AeTitle = _settings.AeTitle,
            Port = _settings.Port,
            StoragePath = _settings.StoragePath,
            IsRunning = _server.IsRunning
        });
    }

    /// <summary>
    /// 启动DICOM服务器
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        try
        {
            if (_server.IsRunning)
            {
                return BadRequest("服务器已经在运行");
            }

            await _server.StartAsync();
            return Ok("服务器已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动服务器失败");
            return StatusCode(500, "启动服务器失败");
        }
    }

    /// <summary>
    /// 停止DICOM服务器
    /// </summary>
    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        try
        {
            if (!_server.IsRunning)
            {
                return BadRequest("服务器未运行");
            }

            await _server.StopAsync();
            return Ok("服务器已停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止服务器失败");
            return StatusCode(500, "停止服务器失败");
        }
    }

    /// <summary>
    /// 更新DICOM服务器配置
    /// </summary>
    [HttpPost("settings")]
    public IActionResult UpdateSettings([FromBody] DicomSettingsUpdate settings)
    {
        try
        {
            if (_server.IsRunning)
            {
                return BadRequest("无法在服务器运行时更新配置");
            }

            // 验证新的存储路径
            if (!string.IsNullOrEmpty(settings.StoragePath))
            {
                try
                {
                    var path = Path.GetFullPath(settings.StoragePath);
                    Directory.CreateDirectory(path);
                    CStoreSCP.Configure(path, settings.AeTitle ?? _settings.AeTitle);
                }
                catch (Exception ex)
                {
                    return BadRequest($"存储路径无效: {ex.Message}");
                }
            }

            return Ok("配置已更新");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新配置失败");
            return StatusCode(500, "更新配置失败");
        }
    }
}

public class DicomSettingsUpdate
{
    public string? StoragePath { get; set; }
    public string? AeTitle { get; set; }
} 