using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using Serilog;
using ILogger = Serilog.ILogger;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FellowOakDicom.Imaging.Codec;

namespace DicomSCP.Services;

public sealed class DicomServer : IDisposable
{
    private static readonly ILogger _logger = Log.ForContext<DicomServer>();
    private readonly DicomSettings _settings;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DicomRepository _repository;
    
    private IDicomServer? _storeScp;
    private IDicomServer? _worklistScp;
    private bool _disposed;

    public DicomServer(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IOptions<DicomSettings> settings,
        DicomRepository repository)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public bool IsRunning => _storeScp != null || _worklistScp != null;

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            _logger.Warning("DICOM服务器已在运行中");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                EnsureStorageDirectory();
                StartDicomServices();
            });
            
            LogServerStartup();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DICOM服务启动失败");
            await StopAsync();  // 确保清理资源
            throw;
        }
    }

    private void EnsureStorageDirectory()
    {
        if (!Directory.Exists(_settings.StoragePath))
        {
            try
            {
                Directory.CreateDirectory(_settings.StoragePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法创建存储目录: {_settings.StoragePath}", ex);
            }
        }
    }

    private void StartDicomServices()
    {
        try
        {
            // 配置和启动 C-STORE SCP 服务器
            CStoreSCP.Configure(
                _settings.StoragePath,
                _settings.TempPath,
                _settings,
                _repository);

            _storeScp = DicomServerFactory.Create<CStoreSCP>(
                _settings.StoreSCPPort,
                null,
                Encoding.UTF8,
                _loggerFactory.CreateLogger<CStoreSCP>(),
                Options.Create(_settings));

            _logger.Information("C-STORE服务已启动 - 端口: {Port}", _settings.StoreSCPPort);

            // 配置和启动 Worklist SCP 服务器
            WorklistSCP.Configure(
                _settings,
                _configuration,
                _repository);

            _worklistScp = DicomServerFactory.Create<WorklistSCP>(
                _settings.WorklistSCPPort,
                null,
                Encoding.UTF8,
                _loggerFactory.CreateLogger<WorklistSCP>(),
                Options.Create(_settings));

            _logger.Information("Worklist服务已启动 - 端口: {Port}", _settings.WorklistSCPPort);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DICOM服务启动失败");
            _storeScp?.Dispose();
            _worklistScp?.Dispose();
            _storeScp = _worklistScp = null;
            throw;
        }
    }

    private void LogServerStartup()
    {
        _logger.Information(
            "DICOM服务启动 - AET: {AET}\n" +
            "C-STORE服务 - 端口: {StorePort}\n" +
            "Worklist服务 - 端口: {WorklistPort}\n" +
            "存储路径: {StoragePath}\n" +
            "{Area}", 
            _settings.AeTitle,
            _settings.StoreSCPPort,
            _settings.WorklistSCPPort,
            Path.GetFullPath(_settings.StoragePath),
            new { Area = "AppState" });
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                _storeScp?.Dispose();
                _worklistScp?.Dispose();
                _storeScp = _worklistScp = null;
            });

            _logger.Information(
                "DICOM服务 - 动作: {Action} {Area}", 
                "停止",
                new { Area = "AppState" });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DICOM服务错误 - 动作: {Action}", "停止");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _storeScp?.Dispose();
        _worklistScp?.Dispose();
        _storeScp = _worklistScp = null;
        _disposed = true;
    }
} 