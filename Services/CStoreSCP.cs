using System.Text;
using System.Collections.Concurrent;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;

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

    // Supported compression transfer syntax mapping
    private static readonly Dictionary<string, DicomTransferSyntax> _compressionSyntaxes = new()
    {
        { "JPEG2000Lossless", DicomTransferSyntax.JPEG2000Lossless },
        { "JPEGLSLossless", DicomTransferSyntax.JPEGLSLossless },
        { "RLELossless", DicomTransferSyntax.RLELossless },
        { "JPEG2000Lossy", DicomTransferSyntax.JPEG2000Lossy },
        { "JPEGProcess14", DicomTransferSyntax.JPEGProcess14SV1 }
    };

    // Tags that need Chinese processing
    private static readonly DicomTag[] TextTags = new[]
    {
        DicomTag.PatientName,           // Patient Name
        DicomTag.StudyDescription,      // Study Description
        DicomTag.SeriesDescription,     // Series Description
        DicomTag.InstitutionName        // Institution Name
    };

    // Tags that need to be directly copied (corresponding to database fields)
    private static readonly DicomTag[] RequiredTags = new[]
    {
        // Basic patient information
        DicomTag.PatientID,
        DicomTag.PatientBirthDate,
        DicomTag.PatientSex,
        DicomTag.AccessionNumber,
        DicomTag.Modality,
        DicomTag.StudyDate,
        DicomTag.StudyTime,
        DicomTag.StudyID,
        DicomTag.SeriesNumber,
        DicomTag.InstanceNumber,

        // Image-related fields
        DicomTag.Columns,
        DicomTag.Rows,
        DicomTag.BitsAllocated,
        DicomTag.BitsStored,
        DicomTag.HighBit,
        DicomTag.PixelRepresentation,
        DicomTag.SamplesPerPixel,
        DicomTag.PhotometricInterpretation,
        DicomTag.SliceThickness,
        DicomTag.SeriesDate,
        DicomTag.ImageType,
        DicomTag.WindowCenter,
        DicomTag.WindowWidth,
        DicomTag.PixelSpacing,
        DicomTag.ImageOrientationPatient,
        DicomTag.ImagePositionPatient,
        DicomTag.FrameOfReferenceUID
    };

    public static void Configure(DicomSettings settings, DicomRepository repository)
    {
        if (string.IsNullOrEmpty(settings.StoragePath) || string.IsNullOrEmpty(settings.TempPath))
        {
            throw new ArgumentException("Storage paths must be configured in settings");
        }

        StoragePath = settings.StoragePath;
        TempPath = settings.TempPath;
        GlobalSettings = settings;
        _repository = repository;

        // Ensure directories exist
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

        // If static paths are not initialized, use values from configuration
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

        DicomLogger.Debug("StoreSCP", "Loaded configuration - Compression: {Enabled}, Format: {Format}",
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
            // Validate Called AE
            var calledAE = association.CalledAE;
            var expectedAE = _settings?.AeTitle ?? string.Empty;

            if (!string.Equals(expectedAE, calledAE, StringComparison.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("StoreSCP", "Rejecting incorrect Called AE: {CalledAE}, Expected: {ExpectedAE}",
                    calledAE, expectedAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            // Validate Calling AE
            if (string.IsNullOrEmpty(association.CallingAE))
            {
                DicomLogger.Warning("StoreSCP", "Rejecting empty Calling AE");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }

            // Only check AllowedCallingAEs if validation is configured
            if (_settings?.Advanced.ValidateCallingAE == true)
            {
                if (!_settings.Advanced.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
                {
                    DicomLogger.Warning("StoreSCP", "Rejecting unauthorized Calling AE: {CallingAE}", association.CallingAE);
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }
            }

            DicomLogger.Debug("StoreSCP", "Validation passed - Called AE: {CalledAE}, Calling AE: {CallingAE}",
                calledAE, association.CallingAE);

            foreach (var pc in association.PresentationContexts)
            {
                if (pc.AbstractSyntax == DicomUID.Verification)  // C-ECHO
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                }
                else if (IsImageStorage(pc.AbstractSyntax))  // Image storage
                {
                    pc.AcceptTransferSyntaxes(_acceptedImageTransferSyntaxes);  // Use image transfer syntaxes
                }
                else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)  // Other storage
                {
                    pc.AcceptTransferSyntaxes(_acceptedTransferSyntaxes);
                }
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCP", ex, "Failed to process association request");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
        }
    }

    private bool IsImageStorage(DicomUID sopClass)
    {
        // Check if it is an image storage category
        if (sopClass.StorageCategory == DicomStorageCategory.Image)
            return true;

        // Check specific image storage SOP classes
        return sopClass.Equals(DicomUID.SecondaryCaptureImageStorage) ||        // Secondary Capture Image
               sopClass.Equals(DicomUID.CTImageStorage) ||                      // CT Image
               sopClass.Equals(DicomUID.MRImageStorage) ||                      // MR Image
               sopClass.Equals(DicomUID.UltrasoundImageStorage) ||              // Ultrasound Image
               sopClass.Equals(DicomUID.UltrasoundMultiFrameImageStorage) ||    // Ultrasound Multi-frame Image
               sopClass.Equals(DicomUID.XRayAngiographicImageStorage) ||        // X-Ray Angiographic Image
               sopClass.Equals(DicomUID.XRayRadiofluoroscopicImageStorage) ||   // X-Ray Radiofluoroscopic Image
               sopClass.Equals(DicomUID.DigitalXRayImageStorageForPresentation) || // Digital X-Ray Image for Presentation
               sopClass.Equals(DicomUID.DigitalMammographyXRayImageStorageForPresentation) || // Digital Mammography X-Ray Image for Presentation
               sopClass.Equals(DicomUID.EnhancedCTImageStorage) ||              // Enhanced CT Image
               sopClass.Equals(DicomUID.EnhancedMRImageStorage) ||              // Enhanced MR Image
               sopClass.Equals(DicomUID.EnhancedXAImageStorage);                // Enhanced XA Image
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Debug("StoreSCP", "Received association release request");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        var sourceDescription = source switch
        {
            DicomAbortSource.ServiceProvider => "Service Provider",
            DicomAbortSource.ServiceUser => "Service User",
            DicomAbortSource.Unknown => "Unknown Source",
            _ => $"Other Source ({source})"
        };

        var reasonDescription = reason switch
        {
            DicomAbortReason.NotSpecified => "Not Specified",
            DicomAbortReason.UnrecognizedPDU => "Unrecognized PDU",
            DicomAbortReason.UnexpectedPDU => "Unexpected PDU",
            _ => $"Other Reason ({reason})"
        };

        DicomLogger.Information("StoreSCP", "Received abort request - Source: {Source} ({SourceDesc}), Reason: {Reason} ({ReasonDesc})",
            source, sourceDescription, reason, reasonDescription);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("StoreSCP", exception, "Connection closed with exception");
        }
        else
        {
            DicomLogger.Debug("StoreSCP", "Connection closed normally");
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

            // Check if it is an image
            if (file.Dataset.InternalTransferSyntax.IsEncapsulated ||
                !IsImageStorage(file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID)))
            {
                return file;
            }

            // Check if the specified compression syntax is supported
            if (!_compressionSyntaxes.TryGetValue(advancedSettings.PreferredTransferSyntax,
                out var targetSyntax))
            {
                DicomLogger.Warning("StoreSCP", "Unsupported compression syntax: {Syntax}", advancedSettings.PreferredTransferSyntax);
                return file;
            }

            // If it is already in the target format, no conversion is needed
            if (file.Dataset.InternalTransferSyntax == targetSyntax)
            {
                return file;
            }

            // Perform compression operation in a background thread
            return await Task.Run(async () =>
            {
                try
                {
                    // Get basic image information
                    var pixelData = DicomPixelData.Create(file.Dataset);
                    if (pixelData == null)
                    {
                        DicomLogger.Warning("StoreSCP", "Unable to get pixel data, skipping compression");
                        return file;
                    }

                    var bitsAllocated = file.Dataset.GetSingleValue<int>(DicomTag.BitsAllocated);
                    var samplesPerPixel = file.Dataset.GetSingleValue<int>(DicomTag.SamplesPerPixel);
                    var photometricInterpretation = file.Dataset.GetSingleValue<string>(DicomTag.PhotometricInterpretation);

                    // Validate based on different compression formats
                    if (targetSyntax == DicomTransferSyntax.JPEGLSLossless)
                    {
                        // JPEG-LS supports 8/12/16 bits
                        if (bitsAllocated != 8 && bitsAllocated != 12 && bitsAllocated != 16)
                        {
                            DicomLogger.Warning("StoreSCP",
                                "JPEG-LS compression requires 8/12/16-bit images, current: {BitsAllocated} bits, skipping compression",
                                bitsAllocated);
                            return file;
                        }
                    }
                    else if (targetSyntax == DicomTransferSyntax.JPEG2000Lossless)
                    {
                        // JPEG2000 supports various bit depths but check if it exceeds 16 bits
                        if (bitsAllocated > 16)
                        {
                            DicomLogger.Warning("StoreSCP",
                                "JPEG2000 compression does not support images over 16 bits, current: {BitsAllocated} bits, skipping compression",
                                bitsAllocated);
                            return file;
                        }
                    }
                    else if (targetSyntax == DicomTransferSyntax.RLELossless)
                    {
                        // RLE compression requires specific bit depths and sampling formats
                        if (bitsAllocated != 8 && bitsAllocated != 16)
                        {
                            DicomLogger.Warning("StoreSCP",
                                "RLE compression requires 8-bit or 16-bit images, current: {BitsAllocated} bits, skipping compression",
                                bitsAllocated);
                            return file;
                        }

                        if (samplesPerPixel > 3)
                        {
                            DicomLogger.Warning("StoreSCP",
                                "RLE compression does not support more than 3 samples per pixel, current: {SamplesPerPixel}, skipping compression",
                                samplesPerPixel);
                            return file;
                        }
                    }

                    DicomLogger.Debug("StoreSCP",
                        "Compressing image - Original format: {OriginalSyntax} -> New format: {NewSyntax}\n  Bit depth: {Bits} bits\n  Samples per pixel: {Samples}\n  Photometric interpretation: {Interpretation}",
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

                        // Validate compression result
                        var compressedPixelData = DicomPixelData.Create(compressedFile.Dataset);
                        if (compressedPixelData == null)
                        {
                            DicomLogger.Error("StoreSCP", "Unable to get pixel data after compression, using original file");
                            return file;
                        }

                        // Check compressed file size
                        using var ms = new MemoryStream();
                        await compressedFile.SaveAsync(ms);
                        var compressedSize = ms.Length;

                        using var originalMs = new MemoryStream();
                        await file.SaveAsync(originalMs);
                        var originalSize = originalMs.Length;

                        DicomLogger.Information("StoreSCP",
                            "Compression completed - Original size: {Original:N0} bytes, Compressed size: {Compressed:N0} bytes, Compression ratio: {Ratio:P2}",
                            originalSize,
                            compressedSize,
                            (originalSize - compressedSize) / (double)originalSize);

                        return compressedFile;
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("StoreSCP", ex, "Image compression failed");
                        return file;
                    }
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("StoreSCP", ex, "Image compression failed");
                    return file;
                }
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCP", ex, "Compression processing failed");
            return file;
        }
    }

    // Modify UID formatting method
    private string FormatUID(string uid)
    {
        try
        {
            if (string.IsNullOrEmpty(uid))
                return uid;

            // Split UID components
            var components = uid.Split('.');
            var formattedComponents = new List<string>();

            foreach (var component in components)
            {
                if (string.IsNullOrEmpty(component))
                    continue;

                // Process each component
                string formattedComponent;
                if (component.Length > 1 && component.StartsWith("0"))
                {
                    // Remove leading zeros but keep a single zero
                    formattedComponent = component.TrimStart('0');
                    if (string.IsNullOrEmpty(formattedComponent))
                    {
                        formattedComponent = "0";
                    }
                }
                else
                {
                    formattedComponent = component;
                }

                // Validate component contains only digits
                if (!formattedComponent.All(char.IsDigit))
                {
                    DicomLogger.Warning("StoreSCP", "UID component contains non-digit characters: {Component}", component);
                    return uid; // Return original value
                }

                formattedComponents.Add(formattedComponent);
            }

            var formattedUid = string.Join(".", formattedComponents);

            // Validate formatted UID
            try
            {
                // Basic validation rules:
                // 1. Cannot be empty
                // 2. Cannot start or end with a dot
                // 3. Cannot have consecutive dots
                // 4. Length cannot exceed 64 characters
                if (string.IsNullOrEmpty(formattedUid) ||
                    formattedUid.StartsWith(".") ||
                    formattedUid.EndsWith(".") ||
                    formattedUid.Contains("..") ||
                    formattedUid.Length > 64)
                {
                    DicomLogger.Warning("StoreSCP", "Formatted UID does not meet rules: {Uid}", formattedUid);
                    return uid;
                }

                // Try creating DicomUID object to validate
                var dicomUid = new DicomUID(formattedUid, "Temp", DicomUidType.Unknown);
                return formattedUid;
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("StoreSCP", ex, "Formatted UID validation failed: {Uid} -> {FormattedUid}",
                    uid, formattedUid);
                return uid; // Return original value
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Warning("StoreSCP", ex, "UID formatting failed: {Uid}", uid);
            return uid;
        }
    }

    // Add a helper method to process UIDs in the dataset
    private void ProcessUID(DicomDataset targetDataset, DicomDataset sourceDataset, DicomTag tag)
    {
        try
        {
            if (!sourceDataset.Contains(tag))
            {
                DicomLogger.Warning("StoreSCP", "UID tag does not exist - Tag: {Tag}", tag);
                return;
            }

            var originalUid = sourceDataset.GetSingleValue<string>(tag);
            var formattedUid = FormatUID(originalUid);

            targetDataset.AddOrUpdate(tag, formattedUid);

            // Add validation log
            DicomLogger.Debug("StoreSCP", "UID processing completed - Tag: {Tag}, Value: {Value}", tag, formattedUid);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCP", ex, "Processing UID failed - Tag: {Tag}", tag);
        }
    }

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        string? tempFilePath = null;
        try
        {
            // Log basic information
            DicomLogger.Debug("StoreSCP",
                "Receiving DICOM file - Patient ID: {PatientId}, Study: {StudyId}, Series: {SeriesId}, Instance: {InstanceUid}",
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

                DicomLogger.Information("StoreSCP", "Received DICOM store request - SOP Class: {SopClass}", request.SOPClassUID.Name);

                var validationResult = ValidateKeyDicomTags(request.Dataset);
                if (!validationResult.IsValid)
                {
                    return new DicomCStoreResponse(request, DicomStatus.InvalidAttributeValue);
                }

                // Get study date, use current date if not available
                var studyDate = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate,
                    DateTime.Now.ToString("yyyyMMdd"));

                // Parse year, month, day
                var year = studyDate.Substring(0, 4);
                var month = studyDate.Substring(4, 2);
                var day = studyDate.Substring(6, 2);

                // Get and format UID
                ProcessUID(request.Dataset, request.Dataset, DicomTag.StudyInstanceUID);
                ProcessUID(request.Dataset, request.Dataset, DicomTag.SeriesInstanceUID);
                ProcessUID(request.Dataset, request.Dataset, DicomTag.SOPInstanceUID);

                var studyUid = request.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
                var seriesUid = request.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID);
                var instanceUid = request.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);

                DicomLogger.Debug("StoreSCP", "Formatted UID - Study: {StudyUid}, Series: {SeriesUid}, Instance: {InstanceUid}",
                    studyUid, seriesUid, instanceUid);

                // Get file lock
                fileLock = _fileLocks.GetOrAdd(instanceUid, _ => new SemaphoreSlim(1, 1));
                await fileLock.WaitAsync();

                try
                {
                    if (TempPath == null || StoragePath == null)
                    {
                        throw new InvalidOperationException("Storage paths are not properly initialized");
                    }

                    // Create temp file in temp directory
                    var tempFileName = $"{instanceUid}_temp_{Guid.NewGuid()}.dcm";
                    tempFilePath = Path.Combine(TempPath, tempFileName);

                    // Compress image
                    var compressedFile = await CompressImageAsync(request.File);

                    // Save compressed file
                    await compressedFile.SaveAsync(tempFilePath);

                    // Build new file path: year/month/day/StudyUID/SeriesUID/SopUID.dcm
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
                        DicomLogger.Warning("StoreSCP", "Duplicate image detected - Path: {FilePath}", targetFilePath);
                        File.Delete(tempFilePath);
                        return new DicomCStoreResponse(request, DicomStatus.DuplicateSOPInstance);
                    }

                    // Log target path before saving
                    DicomLogger.Debug("StoreSCP",
                        "Starting archiving - Study: {StudyUid}, Series: {SeriesUid}, Instance: {InstanceUid}, Path: {Path}",
                        studyUid,
                        seriesUid,
                        instanceUid,
                        targetFilePath);

                    // Move to final location
                    File.Move(tempFilePath, targetFilePath);

                    // Log after successful save
                    DicomLogger.Information("StoreSCP",
                        "Archiving completed - Instance: {InstanceUid}, Path: {FilePath}, Size: {Size:N0} bytes",
                        instanceUid,
                        targetFilePath,
                        new FileInfo(targetFilePath).Length);

                    // Process text fields before saving to database
                    if (_repository != null)
                    {
                        try
                        {
                            var processedDataset = new DicomDataset();
                            processedDataset.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 192");

                            // 1. Process UIDs
                            foreach (var item in request.Dataset)
                            {
                                if (item.ValueRepresentation == DicomVR.UI)
                                {
                                    ProcessUID(processedDataset, request.Dataset, item.Tag);
                                }
                            }

                            // 2. Process Chinese fields
                            foreach (var tag in TextTags)
                            {
                                if (request.Dataset.Contains(tag))
                                {
                                    var value = TryDecodeText(request.Dataset, tag);
                                    if (!string.IsNullOrEmpty(value))
                                    {
                                        processedDataset.AddOrUpdate(tag, value);
                                    }
                                }
                            }

                            // 3. Process other required fields
                            foreach (var tag in RequiredTags)
                            {
                                if (request.Dataset.Contains(tag) && !processedDataset.Contains(tag))  // Avoid duplicate addition
                                {
                                    processedDataset.Add(request.Dataset.GetDicomItem<DicomItem>(tag));
                                }
                            }

                            await _repository.SaveDicomDataAsync(processedDataset, relativePath);

                            // Update Study Modality
                            var modality = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
                            await _repository.UpdateStudyModalityAsync(studyUid, modality);
                        }
                        catch (Exception ex)
                        {
                            DicomLogger.Error("StoreSCP", ex, "Failed to save DICOM data to database");
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
            DicomLogger.Error("StoreSCP", ex, "Failed to save DICOM file - Instance: {InstanceUid}", request.SOPInstanceUID.UID);
            throw;
        }
        finally
        {
            // Ensure temp file is cleaned up
            if (tempFilePath != null && File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("StoreSCP", ex, "Failed to clean up temp file: {TempFile}", tempFilePath);
                }
            }
        }
    }

    private (bool IsValid, string ErrorMessage) ValidateKeyDicomTags(DicomDataset dataset)
    {
        try
        {
            // Define required tags
            var requiredTags = new[]
            {
                (DicomTag.PatientID, "Patient ID"),
                (DicomTag.StudyInstanceUID, "Study Instance UID"),
                (DicomTag.SeriesInstanceUID, "Series Instance UID"),
                (DicomTag.SOPInstanceUID, "SOP Instance UID")
            };

            // Check required tags
            var missingTags = requiredTags
                .Where(t => !dataset.Contains(t.Item1))
                .Select(t => t.Item2)
                .ToList();

            // Check pixel data
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
                var errorMessage = $"DICOM data validation failed: {string.Join(", ", missingTags)}";
                DicomLogger.Warning("StoreSCP", "{ErrorMessage}", errorMessage);
                return (false, errorMessage);
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            var errorMessage = "Exception occurred during DICOM data validation";
            DicomLogger.Error("StoreSCP", ex, "{ErrorMessage}", errorMessage);
            return (false, errorMessage);
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        DicomLogger.Error("StoreSCP", e, "Exception processing C-STORE request - Temp file: {TempFile}", tempFileName);
        return Task.CompletedTask;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Debug("StoreSCP", "Received C-ECHO request");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    // Implement IDisposable pattern
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _concurrentLimit.Dispose();
                // Clean up all file locks
                foreach (var fileLock in _fileLocks.Values)
                {
                    try
                    {
                        fileLock.Dispose();
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("StoreSCP", ex, "Error releasing file lock");
                    }
                }
                _fileLocks.Clear();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    // Add finalizer to prevent resource leaks
    ~CStoreSCP()
    {
        Dispose(false);
    }

    /// <summary>
    /// Try to decode text using different encodings
    /// </summary>
    private string TryDecodeText(DicomDataset dataset, DicomTag tag)
    {
        try
        {
            var element = dataset.GetDicomItem<DicomElement>(tag);
            if (element == null || element.Buffer.Data == null || element.Buffer.Data.Length == 0)
                return string.Empty;

            var bytes = element.Buffer.Data;

            // Try different encodings
            var encodings = new[]
            {
                "UTF-8",
                "GB18030",
                "GB2312",
                "ISO-8859-1"
            };

            foreach (var encodingName in encodings)
            {
                try
                {
                    var encoding = Encoding.GetEncoding(encodingName);
                    var text = encoding.GetString(bytes);

                    // Check if it contains Chinese characters
                    if (text.Any(c => c >= 0x4E00 && c <= 0x9FFF))
                    {
                        DicomLogger.Debug("StoreSCP",
                            "Successfully decoded text - Tag: {Tag}, Encoding: {Encoding}, Text: {Text}",
                            tag, encodingName, text);
                        return text;
                    }
                }
                catch
                {
                    continue;
                }
            }

            // If no Chinese characters detected, return original text
            return dataset.GetString(tag);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCP", ex, "Failed to decode text - Tag: {Tag}", tag);
            return string.Empty;
        }
    }
