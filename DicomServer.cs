using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;

public class DicomServer
{
    private readonly ILogger<DicomServer> _logger;
    private readonly DicomSettings _settings;
    private readonly IServiceProvider _serviceProvider;
    private IDicomServer? _server;
    private readonly ConcurrentDictionary<string, DateTime> _receivedFiles = new();

    public DicomServer(
        ILogger<DicomServer> logger,
        IOptions<DicomSettings> settings,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _settings = settings.Value;
        _serviceProvider = serviceProvider;
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

            // 创建服务器实例
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
            _server.Dispose();
            _server = null;
            _logger.LogInformation("DICOM SCP服务器已停止");
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

    public int GetTotalFilesReceived()
    {
        return _receivedFiles.Count;
    }

    public string GetStorageUsed()
    {
        var directoryInfo = new DirectoryInfo(_settings.StoragePath);
        if (!directoryInfo.Exists) return "0 MB";

        var bytes = directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(file => file.Length);
        return $"{bytes / 1024.0 / 1024.0:F2} MB";
    }
}

public class CStoreSCP : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
{
    private static readonly DicomTransferSyntax[] _acceptedTransferSyntaxes = new DicomTransferSyntax[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    private static readonly DicomTransferSyntax[] _acceptedImageTransferSyntaxes = new DicomTransferSyntax[]
    {
        // Lossless
        DicomTransferSyntax.JPEGLSLossless,
        DicomTransferSyntax.JPEG2000Lossless,
        DicomTransferSyntax.JPEGProcess14SV1,
        DicomTransferSyntax.JPEGProcess14,
        DicomTransferSyntax.RLELossless,
        // Lossy
        DicomTransferSyntax.JPEGLSNearLossless,
        DicomTransferSyntax.JPEG2000Lossy,
        DicomTransferSyntax.JPEGProcess1,
        DicomTransferSyntax.JPEGProcess2_4,
        // Uncompressed
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    // 使用静态配置
    private static string StoragePath = "./received_files";
    private static string AETitle = "STORESCP";

    public CStoreSCP(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
    }

    public static void Configure(string storagePath, string aeTitle)
    {
        StoragePath = storagePath;
        AETitle = aeTitle;
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        Logger.LogInformation($"收到关联请求 - Called AE: {association.CalledAE}, Calling AE: {association.CallingAE}");

        // 不验证 AE Title，接受所有连接请求
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                Logger.LogInformation("接受 C-ECHO 服务");
            }
            else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
            {
                pc.AcceptTransferSyntaxes(_acceptedImageTransferSyntaxes);
                Logger.LogInformation($"接受存储服务: {pc.AbstractSyntax.Name}");
            }
        }

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        Logger.LogInformation("收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        Logger.LogWarning($"收到中止请求 - 来源: {source}, 原因: {reason}");
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            Logger.LogError(exception, "连接异常关闭");
        }
        else
        {
            Logger.LogInformation("连接正常关闭");
        }
    }

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        try
        {
            Logger.LogInformation($"收到 C-STORE 请求 - SOP Class: {request.SOPClassUID.Name}");

            var studyUid = request.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID).Trim();
            var seriesUid = request.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID).Trim();
            var instanceUid = request.SOPInstanceUID.UID;

            var path = Path.Combine(StoragePath, studyUid, seriesUid);
            Directory.CreateDirectory(path);

            var filePath = Path.Combine(path, $"{instanceUid}.dcm");
            
            // 检查文件是否已存在
            if (File.Exists(filePath))
            {
                Logger.LogWarning($"文件已存在: {filePath}");
                return new DicomCStoreResponse(request, DicomStatus.DuplicateSOPInstance);
            }

            await request.File.SaveAsync(filePath);
            Logger.LogInformation($"文件已保存: {filePath}");

            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "处理 C-STORE 请求时发生错误");
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        Logger.LogError(e, $"处理 C-STORE 请求异常 - 临时文件: {tempFileName}");
        return Task.CompletedTask;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        Logger.LogInformation("收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }
} 