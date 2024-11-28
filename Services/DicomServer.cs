using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomSCP.Services;

public class DicomServer : IDisposable
{
    private readonly ILogger<DicomServer> _logger;
    private readonly DicomSettings _settings;
    private IDicomServer? _server;
    private bool _disposed;

    public DicomServer(
        ILogger<DicomServer> logger,
        IOptions<DicomSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    public bool IsRunning => _server != null;

    public Task StartAsync()
    {
        try
        {
            var port = _settings.Port;
            var aet = _settings.AeTitle;

            // 确保存储目录存在
            Directory.CreateDirectory(_settings.StoragePath);

            _server = DicomServerFactory.Create<CStoreSCP>(port);
            
            _logger.LogInformation($"DICOM SCP服务器已启动 - AET: {aet}, 端口: {port}");
            _logger.LogInformation($"存储路径: {_settings.StoragePath}");

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动DICOM SCP服务器失败");
            throw;
        }
    }

    public Task StopAsync()
    {
        if (_server != null)
        {
            try
            {
                _server.Dispose();
                _server = null;
                _logger.LogInformation("DICOM SCP服务器已停止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止DICOM SCP服务器时发生错误");
            }
        }
        return Task.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (_server != null)
                {
                    try
                    {
                        _server.Dispose();
                        _server = null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "释放DICOM服务器资源时发生错误");
                    }
                }
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~DicomServer()
    {
        Dispose(false);
    }
} 