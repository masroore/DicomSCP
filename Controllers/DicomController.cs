using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Data;
using DicomSCP.Models;
using Microsoft.Extensions.Logging;
using System.IO;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DicomController : ControllerBase
{
    private readonly DicomServer _server;
    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;

    public DicomController(
        DicomServer server,
        IOptions<DicomSettings> settings,
        DicomRepository repository)
    {
        _server = server;
        _settings = settings.Value;
        _repository = repository;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        DicomLogger.Debug("Api",
            "[API] 查询服务状态 - AET: {AET}, 端口: {Port}, 路径: {Path}", 
            _settings.AeTitle,
            _settings.StoreSCPPort,
            _settings.StoragePath);

        var serverStatus = _server.GetServicesStatus();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        
        // 获取内存使用情况（MB）
        var memoryUsage = process.WorkingSet64 / 1024.0 / 1024.0;
        
        // 获取CPU使用率 - 跨平台支持
        double cpuUsage = 0;
        try 
        {
            if (OperatingSystem.IsWindows())
            {
                var startTime = process.TotalProcessorTime;
                Thread.Sleep(100); // 等待100ms
                process.Refresh();
                var endTime = process.TotalProcessorTime;
                cpuUsage = (endTime - startTime).TotalMilliseconds / (Environment.ProcessorCount * 100.0);
            }
            else if (OperatingSystem.IsLinux())
            {
                // Linux 下使用 /proc/stat 获取 CPU 使用率
                var startCpu = System.IO.File.ReadAllText("/proc/stat")
                    .Split('\n')[0]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .Take(7)
                    .Select(x => long.Parse(x))
                    .ToArray();
                
                Thread.Sleep(100);
                
                var endCpu = System.IO.File.ReadAllText("/proc/stat")
                    .Split('\n')[0]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .Take(7)
                    .Select(x => long.Parse(x))
                    .ToArray();

                var startIdle = startCpu[3];
                var endIdle = endCpu[3];
                var startTotal = startCpu.Sum();
                var endTotal = endCpu.Sum();

                if (endTotal - startTotal > 0)
                {
                    cpuUsage = (1.0 - (endIdle - startIdle) / (double)(endTotal - startTotal));
                }
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "获取CPU使用率失败");
        }

        return Ok(new
        {
            store = new
            {
                aeTitle = _settings.AeTitle,
                port = _settings.StoreSCPPort,
                isRunning = serverStatus.Services.StoreScp
            },
            worklist = new
            {
                aeTitle = _settings.WorklistSCP.AeTitle,
                port = _settings.WorklistSCP.Port,
                isRunning = serverStatus.Services.WorklistScp
            },
            qr = new
            {
                aeTitle = _settings.QRSCP.AeTitle,
                port = _settings.QRSCP.Port,
                isRunning = serverStatus.Services.QrScp
            },
            print = new
            {
                aeTitle = _settings.PrintSCP.AeTitle,
                port = _settings.PrintSCP.Port,
                isRunning = serverStatus.Services.PrintScp
            },
            system = new
            {
                cpuUsage = Math.Round(cpuUsage * 100, 2),  // 转换为百分比
                memoryUsage = Math.Round(memoryUsage, 2),   // MB
                processorCount = Environment.ProcessorCount,
                processStartTime = process.StartTime,
                osVersion = Environment.OSVersion.ToString(),
                platform = GetPlatformName()
            }
        });
    }

    private string GetPlatformName()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Unknown";
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        try
        {
            if (_server.IsRunning)
            {
                DicomLogger.Warning("Api",
                    "[API] 启动服务失败 - 原因: {Reason}", 
                    "服务器已在运行");
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
            DicomLogger.Information("Api",
                "[API] 启动服务成功");
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
            DicomLogger.Error("Api", ex,
                "[API] 启动服务异常");
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
                DicomLogger.Warning("Api",
                    "[API] 停止服务失败 - 原因: {Reason}", 
                    "服务器未运行");
                return BadRequest("服务器未运行");
            }

            await _server.StopAsync();
            DicomLogger.Information("Api",
                "[API] 停止服务成功");
            return Ok("服务器已停止");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex,
                "[API] 停止服务异常");
            return StatusCode(500, "停止服务器失败");
        }
    }

    [HttpPost("restart")]
    public async Task<IActionResult> Restart()
    {
        try
        {
            DicomLogger.Information("Api", "[API] 正在重启DICOM服务...");
            await _server.RestartAllServices();
            DicomLogger.Information("Api", "[API] DICOM服务重启完成");

            return Ok(new
            {
                Message = "服务重启成功",
                AeTitle = _settings.AeTitle,
                StoreSCPPort = _settings.StoreSCPPort,
                WorklistSCPPort = _settings.WorklistSCP.Port,
                QRSCPPort = _settings.QRSCP.Port
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 重启DICOM服务失败");
            return StatusCode(500, "重启服务失败");
        }
    }
} 