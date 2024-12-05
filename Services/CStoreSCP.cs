using System.Text;
using System.Collections.Concurrent;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
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
        try
        {
            // 验证 Called AE
            var calledAE = association.CalledAE;
            var expectedAE = _settings?.AeTitle ?? string.Empty;

            if (!string.Equals(expectedAE, calledAE, StringComparison.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("StoreSCP", "拒绝错误的 Called AE: {CalledAE}, 期望: {ExpectedAE}", 
                    calledAE, expectedAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            // 验证 Calling AE
            if (string.IsNullOrEmpty(association.CallingAE))
            {
                DicomLogger.Warning("StoreSCP", "拒绝空的 Calling AE");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }

            // 只在配置了验证时才检查 AllowedCallingAEs
            if (_settings?.Advanced.ValidateCallingAE == true)
            {
                if (!_settings.Advanced.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
                {
                    DicomLogger.Warning("StoreSCP", "拒绝未授权的调用方AE: {CallingAE}", association.CallingAE);
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }
            }

            DicomLogger.Information("StoreSCP", "验证通过 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                calledAE, association.CallingAE);

            foreach (var pc in association.PresentationContexts)
            {
                // 检查是否支持请求的服务
                if (pc.AbstractSyntax == DicomUID.Verification ||                    // C-ECHO
                    pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)  // Storage
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                    DicomLogger.Information("StoreSCP", "接受服务 - AET: {CallingAE}, 服务: {Service}", 
                        association.CallingAE, pc.AbstractSyntax.Name);
                }
                else
                {
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                    DicomLogger.Warning("StoreSCP", "拒绝不支持的服务 - AET: {CallingAE}, 服务: {Service}", 
                        association.CallingAE, pc.AbstractSyntax.Name);
                }
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCP", ex, "处理关联请求失败");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
        }
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
        var sourceDescription = source switch
        {
            DicomAbortSource.ServiceProvider => "服务提供方",
            DicomAbortSource.ServiceUser => "服务使用方",
            DicomAbortSource.Unknown => "未知来源",
            _ => $"其他来源({source})"
        };

        var reasonDescription = reason switch
        {
            DicomAbortReason.NotSpecified => "未指定原因",
            DicomAbortReason.UnrecognizedPDU => "无法识别的PDU",
            DicomAbortReason.UnexpectedPDU => "意外的PDU",
            _ => $"其他原因({reason})"
        };

        DicomLogger.Information("StoreSCP", "收到中止请求 - 来源: {Source} ({SourceDesc}), 原因: {Reason} ({ReasonDesc})", 
            source, sourceDescription, reason, reasonDescription);
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
            return await Task.Run(async () =>
            {
                try
                {
                    // 获取图像基本信息
                    var pixelData = DicomPixelData.Create(file.Dataset);
                    if (pixelData == null)
                    {
                        DicomLogger.Warning("StoreSCP", "无法获取像素数据，跳过压缩");
                        return file;
                    }

                    var bitsAllocated = file.Dataset.GetSingleValue<int>(DicomTag.BitsAllocated);
                    var samplesPerPixel = file.Dataset.GetSingleValue<int>(DicomTag.SamplesPerPixel);
                    var photometricInterpretation = file.Dataset.GetSingleValue<string>(DicomTag.PhotometricInterpretation);

                    // 根据不同的压缩格式进行验证
                    if (targetSyntax == DicomTransferSyntax.JPEGLSLossless)
                    {
                        // JPEG-LS 支持 8/12/16 位
                        if (bitsAllocated != 8 && bitsAllocated != 12 && bitsAllocated != 16)
                        {
                            DicomLogger.Warning("StoreSCP", 
                                "JPEG-LS压缩要求8/12/16位图像，当前: {BitsAllocated}位，跳过压缩", 
                                bitsAllocated);
                            return file;
                        }
                    }
                    else if (targetSyntax == DicomTransferSyntax.JPEG2000Lossless)
                    {
                        // JPEG2000 支持多种位深度，但要检查是否超过16位
                        if (bitsAllocated > 16)
                        {
                            DicomLogger.Warning("StoreSCP", 
                                "JPEG2000压缩不支持超过16位的图像，当前: {BitsAllocated}位，跳过压缩", 
                                bitsAllocated);
                            return file;
                        }
                    }
                    else if (targetSyntax == DicomTransferSyntax.RLELossless)
                    {
                        // RLE 压缩要求特定的位深度和采样格式
                        if (bitsAllocated != 8 && bitsAllocated != 16)
                        {
                            DicomLogger.Warning("StoreSCP", 
                                "RLE压缩要求8位或16位图像，当前: {BitsAllocated}位，跳过压缩", 
                                bitsAllocated);
                            return file;
                        }

                        if (samplesPerPixel > 3)
                        {
                            DicomLogger.Warning("StoreSCP", 
                                "RLE压缩不支持超过3个采样/像素，当前: {SamplesPerPixel}，跳过压缩", 
                                samplesPerPixel);
                            return file;
                        }
                    }

                    DicomLogger.Information("StoreSCP",
                        "压缩图像 - 原格式: {OriginalSyntax} -> 新格式: {NewSyntax}\n  位深度: {Bits}位\n  采样数: {Samples}\n  图像解释: {Interpretation}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        targetSyntax.UID.Name,
                        bitsAllocated,
                        samplesPerPixel,
                        photometricInterpretation);

                    try
                    {
                        var transcoder = new DicomTranscoder(
                            file.Dataset.InternalTransferSyntax,
                            targetSyntax);
                        
                        var compressedFile = transcoder.Transcode(file);
                        
                        // 验证压缩结果
                        var compressedPixelData = DicomPixelData.Create(compressedFile.Dataset);
                        if (compressedPixelData == null)
                        {
                            DicomLogger.Error("StoreSCP", "压缩后无法获取像素数据，使用原始文件");
                            return file;
                        }

                        // 检查压缩后的文件大小
                        using var ms = new MemoryStream();
                        await compressedFile.SaveAsync(ms);
                        var compressedSize = ms.Length;
                        
                        using var originalMs = new MemoryStream();
                        await file.SaveAsync(originalMs);
                        var originalSize = originalMs.Length;

                        DicomLogger.Information("StoreSCP", 
                            "压缩完成 - 原始大小: {Original:N0} 字节, 压缩后: {Compressed:N0} 字节, 压缩率: {Ratio:P2}", 
                            originalSize, 
                            compressedSize,
                            (originalSize - compressedSize) / (double)originalSize);

                        return compressedFile;
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("StoreSCP", ex, "图像压缩失败");
                        return file;
                    }
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

                // 获取检查日期，如果没有则使用当前日期
                var studyDate = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, 
                    DateTime.Now.ToString("yyyyMMdd"));
                
                // 解析年月日
                var year = studyDate.Substring(0, 4);
                var month = studyDate.Substring(4, 2);
                var day = studyDate.Substring(6, 2);

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

                    // 构建新的文件路径：年/月/日/StudyUID/SeriesUID/SopUID.dcm
                    var relativePath = Path.Combine(
                        year,
                        month,
                        day,
                        studyUid,
                        seriesUid,
                        $"{instanceUid}.dcm"
                    );

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
                            // 更新 Study 的 Modality
                            var modality = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
                            await _repository.UpdateStudyModalityAsync(studyUid, modality);
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