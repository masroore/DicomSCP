using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;

namespace DicomSCP.Services;

public sealed class DicomServer : IDisposable
{
    private readonly ILogger<DicomServer> _logger;
    private readonly DicomSettings _settings;
    private IDicomServer? _server;

    public DicomServer(ILogger<DicomServer> logger, IOptions<DicomSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    public bool IsRunning => _server != null;

    public Task StartAsync()
    {
        if (_server != null)
        {
            _logger.LogWarning("DICOM服务器已经在运行");
            return Task.CompletedTask;
        }

        try
        {
            Directory.CreateDirectory(_settings.StoragePath);
            _server = DicomServerFactory.Create<CStoreSCP>(_settings.Port);
            _logger.LogInformation("DICOM SCP服务器已启动 - AET: {AET}, 端口: {Port}, 路径: {Path}", 
                _settings.AeTitle, _settings.Port, _settings.StoragePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动DICOM SCP服务器失败");
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try
        {
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
                _logger.LogInformation("DICOM SCP服务器已停止");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止DICOM SCP服务器时发生错误");
            throw;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_server != null)
        {
            _server.Dispose();
            _server = null;
        }
    }
} 