using System.Text;
using System.Collections.Concurrent;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using FellowOakDicom.Imaging;
using Microsoft.Extensions.Logging;

namespace DicomSCP.Services;

public class CStoreSCP : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider, IDisposable
{
    private static readonly DicomTransferSyntax[] _acceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian
    };

    private static readonly DicomTransferSyntax[] _acceptedImageTransferSyntaxes = new[]
    {
        DicomTransferSyntax.JPEGLSLossless,
        DicomTransferSyntax.JPEG2000Lossless,
        DicomTransferSyntax.RLELossless,
        DicomTransferSyntax.JPEGLSNearLossless,
        DicomTransferSyntax.JPEG2000Lossy,
        DicomTransferSyntax.JPEGProcess1,
        DicomTransferSyntax.JPEGProcess2_4,
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian
    };

    private static string? StoragePath;
    private static string? TempPath;
    private static DicomSettings? GlobalSettings;
    private static DicomRepository? _repository;

    private readonly DicomSettings _settings;
    private readonly SemaphoreSlim _concurrentLimit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks;
    private bool _disposed;

    // 支持的压缩传输语法映射
    private static readonly Dictionary<string, DicomTransferSyntax> _compressionSyntaxes = new()
    {
        { "JPEG2000Lossless", DicomTransferSyntax.JPEG2000Lossless },
        { "JPEGLSLossless", DicomTransferSyntax.JPEGLSLossless },
        { "RLELossless", DicomTransferSyntax.RLELossless },
        { "JPEG2000Lossy", DicomTransferSyntax.JPEG2000Lossy },
        { "JPEGProcess14", DicomTransferSyntax.JPEGProcess14SV1 }
    };

    public static void Configure(string storagePath, string tempPath, DicomSettings settings, DicomRepository repository)
    {
        if (string.IsNullOrEmpty(settings.StoragePath) || string.IsNullOrEmpty(settings.TempPath))
        {
            throw new ArgumentException("Storage paths must be configured in settings");
        }
        
        StoragePath = settings.StoragePath;
        TempPath = settings.TempPath;
        GlobalSettings = settings;
        _repository = repository;
        
        // 确保目录存在
        Directory.CreateDirectory(StoragePath);
        Directory.CreateDirectory(TempPath);
    }

    public CStoreSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log, 
        DicomServiceDependencies dependencies,
        IOptions<DicomSettings> settings)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _settings = GlobalSettings ?? settings.Value 
            ?? throw new ArgumentNullException(nameof(settings));
        
        // 如果静态路径未初始化，使用配置中的值
        if (string.IsNullOrEmpty(StoragePath))
        {
            StoragePath = _settings.StoragePath;
            Directory.CreateDirectory(StoragePath);
        }
        if (string.IsNullOrEmpty(TempPath))
        {
            TempPath = _settings.TempPath;
            Directory.CreateDirectory(TempPath);
        }
        
        var advancedSettings = _settings.Advanced;

        DicomLogger.Debug("StoreSCP", "加载配置 - 压缩: {Enabled}, 格式: {Format}", 
            advancedSettings.EnableCompression,
            advancedSettings.PreferredTransferSyntax);

        int concurrentLimit = advancedSettings.ConcurrentStoreLimit > 0 
            ? advancedSettings.ConcurrentStoreLimit 
            : Environment.ProcessorCount * 2;
        _concurrentLimit = new SemaphoreSlim(concurrentLimit);
        _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        DicomLogger.Information("StoreSCP",
            "收到DICOM关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            association.CalledAE, 
            association.CallingAE);

        var advancedSettings = _settings.Advanced;
        
        // 应用验配置
        if (advancedSettings.ValidateCallingAE && 
            !advancedSettings.AllowedCallingAEs.Contains(association.CallingAE))
        {
            DicomLogger.Warning("StoreSCP", "拒绝未授权的调用方AE: {CallingAE}", association.CallingAE);
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.CallingAENotRecognized);
        }

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                DicomLogger.Information("StoreSCP", "DICOM服务 - 动作: {Action}, 类型: {Type}", "接受", "C-ECHO");
            }
            else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
            {
                bool isImageStorage = IsImageStorage(pc.AbstractSyntax);
                
                if (isImageStorage)
                {
                    pc.AcceptTransferSyntaxes(_acceptedImageTransferSyntaxes);
                    DicomLogger.Information("StoreSCP", "DICOM服务 - 动作: {Action}, 类型: {Type}, 传: {Transfer}", 
                        "接受", pc.AbstractSyntax.Name, "支持压缩传输");
                }
                else
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                    DicomLogger.Information("StoreSCP", "接受非图像存储服务: {ServiceName}, {TransferType}", 
                        pc.AbstractSyntax.Name, "仅支持基本传输");
                }
            }
        }

        return SendAssociationAcceptAsync(association);
    }

    private bool IsImageStorage(DicomUID sopClass)
    {
        // 检查是否是图像存储类别
        if (sopClass.StorageCategory == DicomStorageCategory.Image)
            return true;

        // 检查特定的图像存储SOP类
        return sopClass.Equals(DicomUID.SecondaryCaptureImageStorage) ||        // 二次获取图像
               sopClass.Equals(DicomUID.CTImageStorage) ||                      // CT图像
               sopClass.Equals(DicomUID.MRImageStorage) ||                      // MR图像
               sopClass.Equals(DicomUID.UltrasoundImageStorage) ||             // 超声图像
               sopClass.Equals(DicomUID.UltrasoundMultiFrameImageStorage) ||   // 超声多帧图像
               sopClass.Equals(DicomUID.XRayAngiographicImageStorage) ||       // 血管造影图
               sopClass.Equals(DicomUID.XRayRadiofluoroscopicImageStorage) ||  // X射线透视图像
               sopClass.Equals(DicomUID.DigitalXRayImageStorageForPresentation) || // DR图像
               sopClass.Equals(DicomUID.DigitalMammographyXRayImageStorageForPresentation) || // 乳腺X射线图像
               sopClass.Equals(DicomUID.EnhancedCTImageStorage) ||             // 增强CT图像
               sopClass.Equals(DicomUID.EnhancedMRImageStorage) ||             // 增强MR图像
               sopClass.Equals(DicomUID.EnhancedXAImageStorage);               // 增强血管造影图像
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("StoreSCP", "收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("StoreSCP", "收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("StoreSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Information("StoreSCP", "连接正常关闭");
        }
    }

    private async Task<DicomFile> CompressImageAsync(DicomFile file)
    {
        try
        {
            var advancedSettings = _settings.Advanced;
            if (!advancedSettings.EnableCompression)
            {
                return file;
            }

            // 检查是否是图像
            if (file.Dataset.InternalTransferSyntax.IsEncapsulated ||
                !IsImageStorage(file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID)))
            {
                return file;
            }

            // 检查是否支持指定的压缩语法
            if (!_compressionSyntaxes.TryGetValue(advancedSettings.PreferredTransferSyntax, 
                out var targetSyntax))
            {
                DicomLogger.Warning("StoreSCP", "不支持的压缩语法: {Syntax}", advancedSettings.PreferredTransferSyntax);
                return file;
            }

            // 如果已经是目标格式，则不需要转换
            if (file.Dataset.InternalTransferSyntax == targetSyntax)
            {
                return file;
            }

            // 在后台线程执行压缩操作
            return await Task.Run(() =>
            {
                try
                {
                    DicomLogger.Information("StoreSCP",
                        "压缩图像 - 原格式: {OriginalSyntax} -> 新格式: {NewSyntax}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        targetSyntax.UID.Name);

                    var compressedFile = file.Clone();
                    compressedFile.FileMetaInfo.TransferSyntax = targetSyntax;
                    return compressedFile;
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("StoreSCP", ex, "图像压缩失败");
                    return file;
                }
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCP", ex, "压缩处理失败");
            return file;
        }
    }

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        string? tempFilePath = null;
        try
        {
            // 记录基本信息
            DicomLogger.Information("StoreSCP",
                "接收DICOM文件 - 患者ID: {PatientId}, 研究: {StudyId}, 序列: {SeriesId}, 实例: {InstanceUid}",
                request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, "Unknown"),
                request.Dataset.GetSingleValueOrDefault(DicomTag.StudyID, "Unknown"),
                request.Dataset.GetSingleValueOrDefault(DicomTag.SeriesNumber, "Unknown"),
                request.SOPInstanceUID.UID);

            if (_disposed)
            {
                return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
            }

            SemaphoreSlim? fileLock = null;

            try
            {
                await _concurrentLimit.WaitAsync();

                DicomLogger.Information("StoreSCP", "收到DICOM存储请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

                var validationResult = ValidateKeyDicomTags(request.Dataset);
                if (!validationResult.IsValid)
                {
                    return new DicomCStoreResponse(request, DicomStatus.InvalidAttributeValue);
                }

                var studyUid = request.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID).Trim();
                var seriesUid = request.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID).Trim();
                var instanceUid = request.SOPInstanceUID.UID;

                // 获取文件锁
                fileLock = _fileLocks.GetOrAdd(instanceUid, _ => new SemaphoreSlim(1, 1));
                await fileLock.WaitAsync();

                try
                {
                    if (TempPath == null || StoragePath == null)
                    {
                        throw new InvalidOperationException("Storage paths are not properly initialized");
                    }
                    
                    // 使用临时目录创建临时文件
                    var tempFileName = $"{instanceUid}_temp_{Guid.NewGuid()}.dcm";
                    tempFilePath = Path.Combine(TempPath, tempFileName);

                    // 压缩图像
                    var compressedFile = await CompressImageAsync(request.File);

                    // 保存压缩后的文件
                    await compressedFile.SaveAsync(tempFilePath);

                    // 构建相对路径和完整路径
                    var relativePath = Path.Combine(studyUid, seriesUid, $"{instanceUid}.dcm");
                    var targetFilePath = Path.Combine(StoragePath, relativePath);
                    var targetPath = Path.GetDirectoryName(targetFilePath);

                    if (targetPath == null)
                    {
                        throw new InvalidOperationException("Invalid target path structure");
                    }
                    Directory.CreateDirectory(targetPath);

                    if (File.Exists(targetFilePath))
                    {
                        DicomLogger.Warning("StoreSCP", "检测到重复图像 - 路径: {FilePath}", targetFilePath);
                        File.Delete(tempFilePath);
                        return new DicomCStoreResponse(request, DicomStatus.DuplicateSOPInstance);
                    }

                    // 在保存前记录目标路径
                    DicomLogger.Information("StoreSCP",
                        "开始归档 - 研究: {StudyUid}, 序列: {SeriesUid}, 实例: {InstanceUid}, 路径: {Path}",
                        studyUid,
                        seriesUid,
                        instanceUid,
                        targetFilePath);

                    // 移动到最终位置
                    File.Move(tempFilePath, targetFilePath);

                    // 保存成功后记录
                    DicomLogger.Information("StoreSCP",
                        "归档完成 - 实例: {InstanceUid}, 路径: {FilePath}, 大小: {Size:N0} 字节",
                        instanceUid,
                        targetFilePath,
                        new FileInfo(targetFilePath).Length);

                    // 在文件保存成功，保存到数据库
                    if (_repository != null)
                    {
                        try 
                        {
                            await _repository.SaveDicomDataAsync(request.Dataset, relativePath);
                        }
                        catch (Exception ex)
                        {
                            DicomLogger.Error("StoreSCP", ex, "保存DICOM数据到数据库失败");
                        }
                    }

                    return new DicomCStoreResponse(request, DicomStatus.Success);
                }
                finally
                {
                    if (fileLock != null)
                    {
                        fileLock.Release();
                        if (_fileLocks.TryRemove(instanceUid, out var removedLock))
                        {
                            removedLock.Dispose();
                        }
                    }
                }
            }
            finally
            {
                _concurrentLimit.Release();
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCP", ex, "保存DICOM文件失败 - 实例: {InstanceUid}", request.SOPInstanceUID.UID);
            throw;
        }
        finally
        {
            // 确保临时文件被清理
            if (tempFilePath != null && File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("StoreSCP", ex, "清理临时文件失败: {TempFile}", tempFilePath);
                }
            }
        }
    }

    private (bool IsValid, string ErrorMessage) ValidateKeyDicomTags(DicomDataset dataset)
    {
        try
        {
            // 定义必需的标签
            var requiredTags = new[]
            {
                (DicomTag.PatientID, "Patient ID"),
                (DicomTag.StudyInstanceUID, "Study Instance UID"),
                (DicomTag.SeriesInstanceUID, "Series Instance UID"),
                (DicomTag.SOPInstanceUID, "SOP Instance UID")
            };

            // 检查必需标签
            var missingTags = requiredTags
                .Where(t => !dataset.Contains(t.Item1))
                .Select(t => t.Item2)
                .ToList();

            // 检查像素数据
            if (IsImageStorage(dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID)))
            {
                if (!dataset.Contains(DicomTag.PixelData) || 
                    dataset.GetDicomItem<DicomItem>(DicomTag.PixelData) == null)
                {
                    missingTags.Add("Pixel Data (empty or missing)");
                }
            }

            if (missingTags.Any())
            {
                var errorMessage = $"DICOM数据验证失败: {string.Join(", ", missingTags)}";
                DicomLogger.Warning("StoreSCP", "{ErrorMessage}", errorMessage);
                return (false, errorMessage);
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            var errorMessage = "DICOM数据验证过程发生异常";
            DicomLogger.Error("StoreSCP", ex, "{ErrorMessage}", errorMessage);
            return (false, errorMessage);
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        DicomLogger.Error("StoreSCP", e, "处理 C-STORE 请求异常 - 临时文件: {TempFile}", tempFileName);
        return Task.CompletedTask;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("StoreSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    // 实现 IDisposable 模式
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _concurrentLimit.Dispose();
                // 清理所有文件锁
                foreach (var fileLock in _fileLocks.Values)
                {
                    try
                    {
                        fileLock.Dispose();
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("StoreSCP", ex, "释放文件锁时发生错误");
                    }
                }
                _fileLocks.Clear();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    // 添加析构函数以防止资源泄露
    ~CStoreSCP()
    {
        Dispose(false);
    }
} 