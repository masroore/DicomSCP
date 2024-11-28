using System.Text;
using System.Collections.Concurrent;
using FellowOakDicom;
using FellowOakDicom.Network;

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

    // 修改为实例级别的信号量和文件锁，以便能够正确释放
    private readonly SemaphoreSlim _concurrentLimit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks;
    private bool _disposed;

    public static void Configure(string storagePath)
    {
        StoragePath = storagePath;
    }

    public CStoreSCP(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _concurrentLimit = new SemaphoreSlim(Environment.ProcessorCount * 2);
        _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        Logger.LogInformation($"收到关联请求 - Called AE: {association.CalledAE}, Calling AE: {association.CallingAE}");

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                Logger.LogInformation("接受 C-ECHO 服务");
            }
            else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
            {
                bool isImageStorage = IsImageStorage(pc.AbstractSyntax);
                
                if (isImageStorage)
                {
                    pc.AcceptTransferSyntaxes(_acceptedImageTransferSyntaxes);
                    Logger.LogInformation($"接受图像存储服务: {pc.AbstractSyntax.Name}, 支持压缩传输");
                }
                else
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                    Logger.LogInformation($"接受非图像存储服务: {pc.AbstractSyntax.Name}, 仅支持基本传输");
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
        if (_disposed)
        {
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }

        SemaphoreSlim? fileLock = null;
        try
        {
            await _concurrentLimit.WaitAsync();

            Logger.LogInformation($"收到 C-STORE 请求 - SOP Class: {request.SOPClassUID.Name}");

            var validationResult = ValidateKeyDicomTags(request.Dataset);
            if (!validationResult.IsValid)
            {
                Logger.LogWarning($"DICOM标签验证失败: {validationResult.ErrorMessage}");
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
                var path = Path.Combine(StoragePath, studyUid, seriesUid);

                // 创建目录（如果不存在）
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                var filePath = Path.Combine(path, $"{instanceUid}.dcm");
                
                if (File.Exists(filePath))
                {
                    Logger.LogWarning($"文件已存在: {filePath}");
                    return new DicomCStoreResponse(request, DicomStatus.DuplicateSOPInstance);
                }

                // 使用临时文件进行写入
                var tempFilePath = Path.Combine(path, $"{instanceUid}_temp_{Guid.NewGuid()}.dcm");
                try
                {
                    // 保存文件
                    await request.File.SaveAsync(tempFilePath);

                    // 再次检查目标文件是否存在（防止并发）
                    if (File.Exists(filePath))
                    {
                        File.Delete(tempFilePath);
                        Logger.LogWarning($"文件已被其他进程创建: {filePath}");
                        return new DicomCStoreResponse(request, DicomStatus.DuplicateSOPInstance);
                    }

                    // 移动到最终位置
                    File.Move(tempFilePath, filePath);
                    Logger.LogInformation($"文件已保存: {filePath}");
                    return new DicomCStoreResponse(request, DicomStatus.Success);
                }
                catch (Exception ex)
                {
                    // 清理临时文件
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                    Logger.LogError(ex, $"保存文件时发生错误: {tempFilePath}");
                    throw;
                }
            }
            finally
            {
                fileLock.Release();
                // 清理不再使用的文件锁
                if (_fileLocks.TryRemove(instanceUid, out var removedLock))
                {
                    removedLock.Dispose();
                }
            }
        }
        finally
        {
            _concurrentLimit.Release();
            // 确保在异常情况下也能释放文件锁
            if (fileLock != null && _fileLocks.TryRemove(request.SOPInstanceUID.UID, out var lock2))
            {
                lock2.Dispose();
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
                    Logger.LogWarning($"缺少必要标签: {name}");
                }
            }

            if (missingTags.Any())
            {
                var errorMessage = $"缺少必要标签: {string.Join(", ", missingTags)}";
                return (false, errorMessage);
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "验证DICOM标签时发生错误");
            return (false, $"验证标签时发生错误: {ex.Message}");
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
                        Logger.LogError(ex, "释放文件锁时发生错误");
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