using DicomSCP.Data;
using DicomSCP.Configuration;
using DicomSCP.Services;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.Runtime;

namespace DicomSCP.Controllers
{
    [Route("wado")]
    [AllowAnonymous]
    public class WadoURIController : ControllerBase
    {
        private readonly DicomRepository _dicomRepository;
        private readonly DicomSettings _settings;
        private const string AppDicomContentType = "application/dicom";
        private const string JpegImageContentType = "image/jpeg";

        public WadoURIController(
            DicomRepository dicomRepository,
            IOptions<DicomSettings> settings)
        {
            _dicomRepository = dicomRepository;
            _settings = settings.Value;
        }

        [HttpGet]
        public async Task<IActionResult> GetStudyInstances(
            [FromQuery] string? requestType,
            [FromQuery] string studyUID,
            [FromQuery] string seriesUID,
            [FromQuery] string objectUID,
            [FromQuery] string? contentType = default,
            [FromQuery] string? transferSyntax = default,
            [FromQuery] string? anonymize = default)
        {
            // Validate required parameters
            if (string.IsNullOrEmpty(studyUID) || string.IsNullOrEmpty(seriesUID) || string.IsNullOrEmpty(objectUID))
            {
                DicomLogger.Warning("WADO", "Missing required parameters");
                return BadRequest("Missing required parameters");
            }

            DicomLogger.Information("WADO", "Received WADO request - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, ObjectUID: {ObjectUID}, ContentType: {ContentType}, TransferSyntax: {TransferSyntax}",
            studyUID, seriesUID, objectUID, contentType ?? "default", transferSyntax ?? "default");

            // Validate request type (required parameter)
            if (requestType?.ToUpper() != "WADO")
            {
                DicomLogger.Warning("WADO", "Invalid request type: {RequestType}", requestType ?? "null");
                return BadRequest("Invalid requestType - WADO is required");
            }

            try
            {
                // Get instance information from the database
                var instance = await _dicomRepository.GetInstanceAsync(objectUID);
                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "Instance not found: {ObjectUID}", objectUID);
                    return NotFound("Instance not found");
                }

                // Build the full file path
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOM file not found: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // Read the DICOM file
                var dicomFile = await DicomFile.OpenAsync(filePath);

                // Determine the final content type
                string finalContentType = PickFinalContentType(contentType, dicomFile);
                DicomLogger.Information("WADO", "Final content type: {ContentType}", finalContentType);

                // Return based on the requested content type
                if (finalContentType == JpegImageContentType)
                {
                    // If anonymization is requested, process anonymization first
                    if (anonymize == "yes")
                    {
                        DicomLogger.Information("WADO", "Performing anonymization");
                        dicomFile = AnonymizeDicomFile(dicomFile);
                    }

                    // Return JPEG
                    var dicomImage = new DicomImage(dicomFile.Dataset);
                    var renderedImage = dicomImage.RenderImage();

                    // Convert to JPEG
                    byte[] jpegBytes;
                    using (var memoryStream = new MemoryStream())
                    {
                        using var image = Image.LoadPixelData<Rgba32>(
                            renderedImage.AsBytes(),
                            renderedImage.Width,
                            renderedImage.Height);

                        // Configure JPEG encoder options
                        var encoder = new JpegEncoder
                        {
                            Quality = 90  // Set JPEG quality
                        };

                        // Save as JPEG
                        await image.SaveAsJpegAsync(memoryStream, encoder);
                        jpegBytes = memoryStream.ToArray();
                    }

                    DicomLogger.Information("WADO", "Successfully returned JPEG image - Size: {Size} bytes", jpegBytes.Length);

                    // Set the file name to SOP Instance UID
                    var contentDisposition = new System.Net.Mime.ContentDisposition
                    {
                        FileName = $"{objectUID}.jpg",
                        Inline = false  // Use attachment for download
                    };
                    Response.Headers["Content-Disposition"] = contentDisposition.ToString();

                    // Trigger GC proactively
                    if (jpegBytes.Length > 10 * 1024 * 1024) // If the image is larger than 10MB
                    {
                        GC.Collect();
                        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    }

                    return File(jpegBytes, JpegImageContentType);
                }
                else
                {
                    // If anonymization is requested, process anonymization first
                    if (anonymize == "yes")
                    {
                        DicomLogger.Information("WADO", "Performing anonymization");
                        dicomFile = AnonymizeDicomFile(dicomFile);
                    }

                    // Return DICOM (including transfer syntax conversion)
                    var result = await GetDicomBytes(dicomFile, transferSyntax, filePath);

                    // Set the file name to SOP Instance UID
                    var contentDisposition = new System.Net.Mime.ContentDisposition
                    {
                        FileName = $"{objectUID}.dcm",
                        Inline = false  // Use attachment for download
                    };
                    Response.Headers["Content-Disposition"] = contentDisposition.ToString();

                    return result;
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "Error processing WADO request");
                return StatusCode(500, $"Error processing image: {ex.Message}");
            }
        }

        private string PickFinalContentType(string? contentType, DicomFile dicomFile)
        {
            // If no content type is specified, choose the default based on the image type
            if (string.IsNullOrEmpty(contentType))
            {
                // Get the number of frames
                var numberOfFrames = dicomFile.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);
                // Default to DICOM for multi-frame images, JPEG for single-frame images
                return numberOfFrames > 1 ? AppDicomContentType : JpegImageContentType;
            }

            return contentType;
        }

        private async Task<IActionResult> GetDicomBytes(DicomFile dicomFile, string? transferSyntax, string filePath)
        {
            try
            {
                // If transfer syntax conversion is needed
                if (!string.IsNullOrEmpty(transferSyntax))
                {
                    try
                    {
                        var currentSyntax = dicomFile.Dataset.InternalTransferSyntax;
                        var requestedSyntax = GetRequestedTransferSyntax(transferSyntax);

                        DicomLogger.Information("WADO", "Transfer Syntax - Current: {CurrentSyntax}, Requested: {RequestedSyntax}",
                            currentSyntax.UID.Name,
                            requestedSyntax.UID.Name);

                        if (currentSyntax != requestedSyntax)
                        {
                            try
                            {
                                var transcoder = new DicomTranscoder(currentSyntax, requestedSyntax);
                                dicomFile = transcoder.Transcode(dicomFile);
                                DicomLogger.Information("WADO", "Converted transfer syntax to: {NewSyntax}", requestedSyntax.UID.Name);

                                // After transcoding, regenerate the byte stream
                                using var ms = new MemoryStream();
                                await dicomFile.SaveAsync(ms);
                                return File(ms.ToArray(), AppDicomContentType);
                            }
                            catch (Exception ex)
                            {
                                DicomLogger.Error("WADO", ex, "Failed to convert transfer syntax: {TransferSyntax}", transferSyntax);
                                throw new InvalidOperationException($"Unable to convert to requested transfer syntax: {transferSyntax}", ex);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not InvalidOperationException)
                    {
                        DicomLogger.Warning("WADO", ex, "Invalid transfer syntax request: {TransferSyntax}", transferSyntax);
                    }
                }

                // If no transfer syntax conversion is needed, return the file stream directly
                var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                DicomLogger.Information("WADO", "Returning DICOM file using file stream: {FilePath}", filePath);
                return File(fileStream, AppDicomContentType);
            }
            catch
            {
                throw;
            }
        }

        private DicomTransferSyntax GetRequestedTransferSyntax(string syntax)
        {
            try
            {
                // Try to directly parse the transfer syntax UID
                return DicomTransferSyntax.Parse(syntax);
            }
            catch
            {
                // If parsing fails, use common transfer syntax UID mappings
                return syntax switch
                {
                    // Uncompressed
                    "1.2.840.10008.1.2" => DicomTransferSyntax.ImplicitVRLittleEndian,
                    "1.2.840.10008.1.2.1" => DicomTransferSyntax.ExplicitVRLittleEndian,
                    "1.2.840.10008.1.2.2" => DicomTransferSyntax.ExplicitVRBigEndian,

                    // JPEG Baseline
                    "1.2.840.10008.1.2.4.50" => DicomTransferSyntax.JPEGProcess1,
                    "1.2.840.10008.1.2.4.51" => DicomTransferSyntax.JPEGProcess2_4,

                    // JPEG Lossless
                    "1.2.840.10008.1.2.4.57" => DicomTransferSyntax.JPEGProcess14,
                    "1.2.840.10008.1.2.4.70" => DicomTransferSyntax.JPEGProcess14SV1,

                    // JPEG 2000
                    "1.2.840.10008.1.2.4.90" => DicomTransferSyntax.JPEG2000Lossless,
                    "1.2.840.10008.1.2.4.91" => DicomTransferSyntax.JPEG2000Lossy,

                    // JPEG-LS
                    "1.2.840.10008.1.2.4.80" => DicomTransferSyntax.JPEGLSLossless,
                    "1.2.840.10008.1.2.4.81" => DicomTransferSyntax.JPEGLSNearLossless,

                    // RLE
                    "1.2.840.10008.1.2.5" => DicomTransferSyntax.RLELossless,

                    // Default to Explicit VR Little Endian if unknown
                    _ => DicomTransferSyntax.ExplicitVRLittleEndian
                };
            }
        }

        private DicomFile AnonymizeDicomFile(DicomFile dicomFile)
        {
            // Clone the dataset
            var newDataset = dicomFile.Dataset.Clone();

            // Basic tag anonymization
            newDataset.AddOrUpdate(DicomTag.PatientName, "ANONYMOUS");
            newDataset.AddOrUpdate(DicomTag.PatientID, "ANONYMOUS");
            newDataset.AddOrUpdate(DicomTag.PatientBirthDate, "19000101");

            // Remove sensitive tags
            newDataset.Remove(DicomTag.PatientAddress);
            newDataset.Remove(DicomTag.PatientTelephoneNumbers);
            newDataset.Remove(DicomTag.PatientMotherBirthName);
            newDataset.Remove(DicomTag.OtherPatientIDsSequence);
            newDataset.Remove(DicomTag.OtherPatientNames);
            newDataset.Remove(DicomTag.PatientComments);
            newDataset.Remove(DicomTag.InstitutionName);
            newDataset.Remove(DicomTag.ReferringPhysicianName);
            newDataset.Remove(DicomTag.PerformingPhysicianName);
            newDataset.Remove(DicomTag.NameOfPhysiciansReadingStudy);
            newDataset.Remove(DicomTag.OperatorsName);

            // Modify study and series descriptions
            newDataset.AddOrUpdate(DicomTag.StudyDescription, "ANONYMOUS");
            newDataset.AddOrUpdate(DicomTag.SeriesDescription, "ANONYMOUS");

            // Create a new DicomFile
            var anonymizedFile = new DicomFile(newDataset);

            // Copy file meta information
            anonymizedFile.FileMetaInfo.TransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;
            anonymizedFile.FileMetaInfo.MediaStorageSOPClassUID = dicomFile.FileMetaInfo.MediaStorageSOPClassUID;
            anonymizedFile.FileMetaInfo.MediaStorageSOPInstanceUID = dicomFile.FileMetaInfo.MediaStorageSOPInstanceUID;

            return anonymizedFile;
        }
    }
}
