using System.Text;
using System.Collections.Concurrent;
using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;
using ILogger = Serilog.ILogger;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;

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

    private static string StoragePath = "./received_files";
    private static string TempPath = "./temp_files";
    private static DicomSettings? GlobalSettings;

    private readonly DicomSettings _settings;
    private readonly SemaphoreSlim _concurrentLimit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks;
    private readonly ILogger _logger = Log.ForContext<CStoreSCP>();
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

    public static void Configure(string storagePath, string tempPath, DicomSettings settings)
    {
        StoragePath = storagePath;
        TempPath = tempPath;
        GlobalSettings = settings;
        
        // 确保临时目录存在
        Directory.CreateDirectory(tempPath);
    }

    public CStoreSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log, 
        DicomServiceDependencies dependencies,
        IOptions<DicomSettings> settings)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _settings = GlobalSettings ?? settings.Value;
        var advancedSettings = _settings.Advanced;

        // 添加配置检查日志
        _logger.Debug("加载配置 - 压缩: {Enabled}, 格式: {Format}", 
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
        _logger.Information(
            "收到DICOM关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            association.CalledAE, 
            association.CallingAE);

        var advancedSettings = _settings.Advanced;
        
        // 应用验证配置
        if (advancedSettings.ValidateCallingAE && 
            !advancedSettings.AllowedCallingAEs.Contains(association.CallingAE))
        {
            _logger.Warning("拒绝未授权的调用方AE: {CallingAE}", association.CallingAE);
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
                _logger.Information("DICOM服务 - 动作: {Action}, 类型: {Type}", "接受", "C-ECHO");
            }
            else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
            {
                bool isImageStorage = IsImageStorage(pc.AbstractSyntax);
                
                if (isImageStorage)
                {
                    pc.AcceptTransferSyntaxes(_acceptedImageTransferSyntaxes);
                    _logger.Information("DICOM服务 - 动作: {Action}, 类型: {Type}, 传输: {Transfer}", 
                        "接受", pc.AbstractSyntax.Name, "支持压缩传输");
                }
                else
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                    _logger.Information("接受非图像存储服务: {ServiceName}, {TransferType}", 
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
               sopClass.Equals(DicomUID.XRayAngiographicImageStorage) ||       // 血管造影图像
               sopClass.Equals(DicomUID.XRayRadiofluoroscopicImageStorage) ||  // X射线透视图像
               sopClass.Equals(DicomUID.DigitalXRayImageStorageForPresentation) || // DR图像
               sopClass.Equals(DicomUID.DigitalMammographyXRayImageStorageForPresentation) || // 乳腺X射线图像
               sopClass.Equals(DicomUID.EnhancedCTImageStorage) ||             // 增强CT图像
               sopClass.Equals(DicomUID.EnhancedMRImageStorage) ||             // 增强MR图像
               sopClass.Equals(DicomUID.EnhancedXAImageStorage);               // 增强血管造影图像
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        _logger.Information("收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        _logger.Warning("收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            _logger.Error(exception, "连接异常关闭");
        }
        else
        {
            _logger.Information("连接正常关闭");
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
                _logger.Warning("不支持的压缩语法: {Syntax}", advancedSettings.PreferredTransferSyntax);
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
                    _logger.Information(
                        "压缩图像 - 原格式: {OriginalSyntax} -> 新格式: {NewSyntax}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        targetSyntax.UID.Name);

                    var compressedFile = file.Clone();
                    compressedFile.FileMetaInfo.TransferSyntax = targetSyntax;
                    return compressedFile;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "图像压缩失败");
                    return file;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "图像压缩失败");
            return file;
        }
    }

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        string? tempFilePath = null;
        try
        {
            // 记录基本信息
            _logger.Information(
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

                _logger.Information("收到DICOM存储请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

                var validationResult = ValidateKeyDicomTags(request.Dataset);
                if (!validationResult.IsValid)
                {
                    _logger.Warning("DICOM标签验证失败 - 原因: {Reason}", validationResult.ErrorMessage);
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
                    // 使用临时目录创建临时文件
                    var tempFileName = $"{instanceUid}_temp_{Guid.NewGuid()}.dcm";
                    tempFilePath = Path.Combine(TempPath, tempFileName);

                    // 压缩图像
                    var compressedFile = await CompressImageAsync(request.File);

                    // 保存压缩后的文件
                    await compressedFile.SaveAsync(tempFilePath);

                    // 确保目标目录存在
                    var targetPath = Path.Combine(StoragePath, studyUid, seriesUid);
                    Directory.CreateDirectory(targetPath);
                    var targetFilePath = Path.Combine(targetPath, $"{instanceUid}.dcm");

                    if (File.Exists(targetFilePath))
                    {
                        _logger.Warning("检测到重复图像 - 路径: {FilePath}", targetFilePath);
                        File.Delete(tempFilePath);
                        return new DicomCStoreResponse(request, DicomStatus.DuplicateSOPInstance);
                    }

                    // 在保存前记录目标路径
                    _logger.Information(
                        "开始归档 - 研究: {StudyUid}, 序列: {SeriesUid}, 实例: {InstanceUid}, 路径: {Path}",
                        studyUid,
                        seriesUid,
                        instanceUid,
                        targetFilePath);

                    // 移动到最终位置
                    File.Move(tempFilePath, targetFilePath);

                    // 保存成功后记录
                    _logger.Information(
                        "归档完成 - 实例: {InstanceUid}, 路径: {FilePath}, 大小: {Size:N0} 字节",
                        instanceUid,
                        targetFilePath,
                        new FileInfo(targetFilePath).Length);

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
            _logger.Error(ex, "保存DICOM文件失败 - 实例: {InstanceUid}", request.SOPInstanceUID.UID);
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
                    _logger.Error(ex, "清理临时文件失败: {TempFile}", tempFilePath);
                }
            }
        }
    }

    private (bool IsValid, string ErrorMessage) ValidateKeyDicomTags(DicomDataset dataset)
    {
        try
        {
            var requiredTags = new[]
            {
                (DicomTag.PatientID, "Patient ID"),
                (DicomTag.StudyInstanceUID, "Study Instance UID"),
                (DicomTag.SeriesInstanceUID, "Series Instance UID"),
                (DicomTag.SOPInstanceUID, "SOP Instance UID")
            };

            var missingTags = new List<string>();

            foreach (var (tag, name) in requiredTags)
            {
                if (!dataset.Contains(tag))
                {
                    missingTags.Add(name);
                    _logger.Warning("缺少必要标签: {TagName}", name);
                }
            }

            if (missingTags.Any())
            {
                var tags = string.Join(", ", missingTags);
                _logger.Warning("DICOM标签验证失败 - 缺失标签: {Tags}", tags);
                return (false, $"缺少必要标签: {tags}");
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "验证DICOM标签失败");
            return (false, "验证DICOM标签失败");
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        _logger.Error(e, "处理 C-STORE 请求异常 - 临时文件: {TempFile}", tempFileName);
        return Task.CompletedTask;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        _logger.Information("收到 C-ECHO 请求");
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
                        _logger.Error(ex, "释放文件锁时发生错误");
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