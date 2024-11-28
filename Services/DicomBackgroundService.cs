using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class DicomBackgroundService : BackgroundService
{
    private readonly DicomServer _dicomServer;
    private readonly ILogger<DicomBackgroundService> _logger;

    public DicomBackgroundService(
        DicomServer dicomServer,
        ILogger<DicomBackgroundService> logger)
    {
        _dicomServer = dicomServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _dicomServer.StartAsync();
            _logger.LogInformation("DICOM SCP服务已在后台启动");

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DICOM SCP服务发生错误");
            throw;
        }
        finally
        {
            await _dicomServer.StopAsync();
        }
    }
} 