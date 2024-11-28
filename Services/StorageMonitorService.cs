using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace DicomSCP.Services;

public class StorageMonitorService : BackgroundService
{
    private readonly ILogger<StorageMonitorService> _logger;
    private readonly DicomSettings _settings;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);
    private bool _disposed;

    public StorageMonitorService(
        ILogger<StorageMonitorService> logger,
        IOptions<DicomSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    await Task.Run(() => CheckStorageStatus(), stoppingToken);
                    await Task.Delay(_checkInterval, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "存储监控发生错误");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常的取消操作，不需要特殊处理
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "存储监控服务发生错误");
            throw;
        }
    }

    private void CheckStorageStatus()
    {
        var path = _settings.StoragePath;
        var drive = new DriveInfo(Path.GetPathRoot(path) ?? "C:");
        
        var freeSpaceGB = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        var totalSpaceGB = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
        var usedPercentage = ((totalSpaceGB - freeSpaceGB) / totalSpaceGB) * 100;

        _logger.LogInformation(
            "存储状态 - 总空间: {Total:F1}GB, 可用空间: {Free:F1}GB, 使用率: {Used:F1}%",
            totalSpaceGB, freeSpaceGB, usedPercentage);

        if (freeSpaceGB < 1.0)
        {
            _logger.LogCritical("存储空间严重不足，可用空间小于1GB");
        }
        else if (freeSpaceGB < 5.0)
        {
            _logger.LogWarning("存储空间不足，可用空间小于5GB");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _disposed = true;
            await base.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止存储监控服务时发生错误");
            throw;
        }
    }
} 