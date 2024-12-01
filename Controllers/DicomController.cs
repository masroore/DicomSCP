using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Data;
using DicomSCP.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DicomController : ControllerBase
{
    private readonly DicomServer _server;
    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;
    private readonly ILogger _logger;

    public DicomController(
        DicomServer server,
        IOptions<DicomSettings> settings,
        DicomRepository repository,
        Microsoft.Extensions.Logging.ILogger<DicomController> logger)
    {
        _server = server;
        _settings = settings.Value;
        _repository = repository;
        _logger = Log.ForContext<DicomController>();
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        _logger.Debug("API操作 - 动作: {Action}, AET: {AET}, 端口: {Port}, 路径: {Path}", 
            "查询状态",
            _settings.AeTitle,
            _settings.StoreSCPPort,
            _settings.StoragePath);

        return Ok(new
        {
            _settings.AeTitle,
            _settings.StoreSCPPort,
            WorklistSCPPort = _settings.WorklistSCP.Port,
            _settings.StoragePath,
            IsRunning = _server.IsRunning
        });
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        try
        {
            if (_server.IsRunning)
            {
                _logger.Warning("API操作 - 动作: {Action}, 状态: {Status}, 原因: {Reason}", 
                    "启动服务器", "失败", "服务器已在运行");
                return BadRequest(new
                {
                    Message = "服务器已在运行",
                    AeTitle = _settings.AeTitle,
                    StoreSCPPort = _settings.StoreSCPPort,
                    WorklistSCPPort = _settings.WorklistSCP.Port
                });
            }

            CStoreSCP.Configure(
                _settings.StoragePath,
                _settings.TempPath,
                _settings,
                _repository
            );

            await _server.StartAsync();
            _logger.Information("API操作 - 动作: {Action}, 结果: {Result}", 
                "启动服务器", "成功");
            return Ok(new
            {
                Message = "服务器已启动",
                AeTitle = _settings.AeTitle,
                StoreSCPPort = _settings.StoreSCPPort,
                WorklistSCPPort = _settings.WorklistSCP.Port
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "API操作 - 动作: {Action}, 状态: {Status}", 
                "启动服务器", "异常");
            return StatusCode(500, "启动服务器失败");
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        try
        {
            if (!_server.IsRunning)
            {
                _logger.Warning("API操作 - 动作: {Action}, 状态: {Status}, 原因: {Reason}", 
                    "停止服务器", "失败", "服务器未运行");
                return BadRequest("服务器未运行");
            }

            await _server.StopAsync();
            _logger.Information("API操作 - 动作: {Action}, 结果: {Result}", 
                "停止服务器", "成功");
            return Ok("服务器已停止");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "API操作 - 动作: {Action}, 状态: {Status}", 
                "停止服务器", "异常");
            return StatusCode(500, "停止服务器失败");
        }
    }
} 