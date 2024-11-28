using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using Serilog;
using ILogger = Serilog.ILogger;

namespace DicomSCP.Services;

public sealed class DicomServer : IDisposable
{
    private static readonly ILogger _logger = Log.ForContext<DicomServer>();
    private readonly DicomSettings _settings;
    private IDicomServer? _server;

    public DicomServer(Microsoft.Extensions.Logging.ILogger<DicomServer> logger, IOptions<DicomSettings> settings)
    {
        _settings = settings.Value;
    }

    public bool IsRunning => _server != null;

    public async Task StartAsync()
    {
        if (_server != null)
        {
            _logger.Warning("DICOM服务器已在运行中");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(_settings.StoragePath);
                _server = DicomServerFactory.Create<CStoreSCP>(_settings.Port);
            });
            
            _logger.Information(
                "DICOM服务 - 动作: {Action}, AET: {AET}, 端口: {Port}, 路径: {Path} {Area}", 
                "启动",
                _settings.AeTitle, 
                _settings.Port, 
                _settings.StoragePath,
                new { Area = "AppState" });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DICOM服务启动失败");
            throw;
        }
    }

    public Task StopAsync()
    {
        try
        {
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
                _logger.Information(
                    "DICOM服务 - 动作: {Action} {Area}", 
                    "停止",
                    new { Area = "AppState" });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DICOM服务错误 - 动作: {Action}", "停止");
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