using System.Text;
using System.Collections.Concurrent;
using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;
using ILogger = Serilog.ILogger;

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
    private readonly ILogger _logger = Log.ForContext<CStoreSCP>();
    private bool _disposed;

    public static void Configure(string storagePath)
    {
        StoragePath = storagePath;
    }

    public CStoreSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log, 
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _concurrentLimit = new SemaphoreSlim(Environment.ProcessorCount * 2);
        _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        _logger.Information(
            "收到DICOM关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            association.CalledAE, 
            association.CallingAE);

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

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        try
        {
            // 记录基本信息
            _logger.Information(
                "接收DICOM文件 - 患者ID: {PatientId}, 研究: {StudyId}, 序列: {SeriesId}",
                request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, "Unknown"),
                request.Dataset.GetSingleValueOrDefault(DicomTag.StudyID, "Unknown"),
                request.Dataset.GetSingleValueOrDefault(DicomTag.SeriesNumber, "Unknown")
            );

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
                    var path = Path.Combine(StoragePath, studyUid, seriesUid);

                    // 创建目录（如果不存在）
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }

                    var filePath = Path.Combine(path, $"{instanceUid}.dcm");
                    
                    if (File.Exists(filePath))
                    {
                        _logger.Warning("文件已存在 - 路径: {FilePath}", filePath);
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
                            _logger.Warning("文件已被其他进程创建 - 路径: {FilePath}", filePath);
                            return new DicomCStoreResponse(request, DicomStatus.DuplicateSOPInstance);
                        }

                        // 移动到最终位置
                        File.Move(tempFilePath, filePath);
                        _logger.Information("文件保存成功 - 路径: {FilePath}", filePath);
                        return new DicomCStoreResponse(request, DicomStatus.Success);
                    }
                    catch (Exception ex)
                    {
                        // 清理临时文件
                        if (File.Exists(tempFilePath))
                        {
                            File.Delete(tempFilePath);
                        }
                        _logger.Error(ex, "保存文件时发生错误 - 路径: {FilePath}", tempFilePath);
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
        catch (Exception ex)
        {
            _logger.Error(ex, "保存DICOM文件失败");
            throw;
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