using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DicomSCP.Models;
using System.Net;

namespace DicomSCP.Services;

public sealed class DicomServer : IDisposable
{
    private readonly DicomSettings _settings;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DicomRepository _repository;
    
    private IDicomServer? _storeScp;
    private IDicomServer? _worklistScp;
    private IDicomServer? _qrScp;
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

    public bool IsRunning => _storeScp != null || _worklistScp != null || _qrScp != null;

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            DicomLogger.Warning("DICOM", "DICOM服务器已在运行中");
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
            DicomLogger.Error("DICOM", ex, "DICOM服务启动失败");
            await StopAsync();
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
                DicomLogger.Information("DICOM", "创建存储目录: {Path}", _settings.StoragePath);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("DICOM", ex, "创建存储目录失败: {Path}", _settings.StoragePath);
                throw;
            }
        }
    }

    private void StartDicomServices()
    {
        try
        {
            DicomLogger.Information("DICOM", "开始启动DICOM服务...");

            // 配置存储服务
            CStoreSCP.Configure(
                _settings.StoragePath,
                _settings.TempPath,
                _settings,
                _repository);

            // 配置工作列表服务
            WorklistSCP.Configure(
                _settings,
                _configuration,
                _repository);

            // 启动存储服务
            _storeScp = DicomServerFactory.Create<CStoreSCP>(
                _settings.StoreSCPPort,
                null,
                Encoding.UTF8,
                _loggerFactory.CreateLogger<CStoreSCP>());

            DicomLogger.Information("DICOM", "C-STORE服务已启动 - AET: {AeTitle}, 端口: {Port}", 
                _settings.AeTitle, _settings.StoreSCPPort);

            // 启动工作列表服务
            _worklistScp = DicomServerFactory.Create<WorklistSCP>(
                _settings.WorklistSCP.Port,
                null,
                Encoding.UTF8,
                _loggerFactory.CreateLogger<WorklistSCP>());

            DicomLogger.Information("DICOM", "Worklist服务已启动 - AET: {AeTitle}, 端口: {Port}", 
                _settings.WorklistSCP.AeTitle, _settings.WorklistSCP.Port);

            // 启动QR服务
            _qrScp = DicomServerFactory.Create<QRSCP>(
                _settings.QRSCP.Port,
                null,
                Encoding.UTF8,
                _loggerFactory.CreateLogger<QRSCP>(),
                _repository);

            DicomLogger.Information("DICOM", "QR服务已启动 - AET: {AeTitle}, 端口: {Port}", 
                _settings.QRSCP.AeTitle, _settings.QRSCP.Port);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOM", ex, "DICOM服务启动失败 - AET: {AeTitle}", _settings.AeTitle);
            _storeScp?.Dispose();
            _worklistScp?.Dispose();
            _qrScp?.Dispose();
            _storeScp = _worklistScp = null;
            _qrScp = null;
            throw;
        }
    }

    private void LogServerStartup()
    {
        DicomLogger.Information("DICOM", "DICOM服务启动完成...");
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
                DicomLogger.Information("DICOM", "正在停止DICOM服务...");
                _storeScp?.Dispose();
                _worklistScp?.Dispose();
                _qrScp?.Dispose();
                _storeScp = _worklistScp = null;
                _qrScp = null;
            });
            DicomLogger.Information("DICOM", "DICOM服务已停止...");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOM", ex, "停止DICOM服务时发生错误!");
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
        _qrScp?.Dispose();
        _disposed = true;
    }
} 