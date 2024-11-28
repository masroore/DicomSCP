using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

[ApiController]
[Route("api/[controller]")]
public class DicomController : ControllerBase
{
    private readonly DicomServer _dicomServer;
    private readonly DicomSettings _settings;
    private readonly ILogger<DicomController> _logger;

    public DicomController(
        DicomServer dicomServer,
        IOptions<DicomSettings> settings,
        ILogger<DicomController> logger)
    {
        _dicomServer = dicomServer;
        _settings = settings.Value;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            AeTitle = _settings.AeTitle,
            Port = _settings.Port,
            StoragePath = _settings.StoragePath,
            IsRunning = true // 你可能需要在DicomServer中添加状态属性
        });
    }

    [HttpGet("statistics")]
    public IActionResult GetStatistics()
    {
        // 这里可以添加统计信息，如接收文件数量等
        return Ok(new
        {
            TotalFilesReceived = 0, // 需要在DicomServer中实现统计
            StorageUsed = "0 MB"    // 需要实现存储空间统计
        });
    }
} 