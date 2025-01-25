using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DicomSCP.Data;
using DicomSCP.Configuration;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using DicomSCP.Services;

namespace DicomSCP.Controllers
{
    [ApiController]
    [Route("dicomweb")]
    [AllowAnonymous]
    public class WadoRsController : ControllerBase
    {
        private readonly DicomRepository _repository;
        private readonly DicomSettings _settings;
        private const string AppDicomContentType = "application/dicom";
        private const string JpegImageContentType = "image/jpeg";

        public WadoRsController(DicomRepository repository, IOptions<DicomSettings> settings)
        {
            _repository = repository;
            _settings = settings.Value;
        }

        #region WADO-RS Retrieval Interfaces

        // WADO-RS: Retrieve Study
        [HttpGet("studies/{studyInstanceUid}")]
        [Produces("multipart/related", "application/dicom")]
        public async Task<IActionResult> RetrieveStudy(string studyInstanceUid)
        {
            try
            {
                // Log request information
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", "DICOMweb - Received study retrieval request - StudyUID: {StudyUID}, Accept: {Accept}",
                    studyInstanceUid, acceptHeader);

                // Get all instances in the study
                var instances = await Task.FromResult(_repository.GetInstancesByStudyUid(studyInstanceUid));
                if (!instances.Any())
                {
                    DicomLogger.Warning("WADO", "DICOMweb - Study not found: {StudyUID}", studyInstanceUid);
                    return NotFound("Study not found");
                }

                // Sort by series number and instance number
                instances = instances
                    .OrderBy(i => int.Parse(_repository.GetSeriesByStudyUid(studyInstanceUid)
                    .First(s => s.SeriesInstanceUid == i.SeriesInstanceUid)
                    .SeriesNumber ?? "0"))
                    .ThenBy(i => int.Parse(i.InstanceNumber ?? "0"))
                    .ToList();

                // Determine transfer syntax
                DicomTransferSyntax targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));

                if (transferSyntaxPart != null)
                {
                    var transferSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                    if (transferSyntax == "*")
                    {
                        // Use the original transfer syntax of the first instance
                        var firstInstance = instances.First();
                        var firstFilePath = Path.Combine(_settings.StoragePath, firstInstance.FilePath);
                        var firstDicomFile = await DicomFile.OpenAsync(firstFilePath);
                        targetTransferSyntax = firstDicomFile.FileMetaInfo.TransferSyntax;
                    }
                    else
                    {
                        targetTransferSyntax = DicomTransferSyntax.Parse(transferSyntax);
                    }
                }

                // Create multipart/related response
                var boundary = $"boundary.{Guid.NewGuid():N}";
                var responseStream = new MemoryStream();
                var writer = new StreamWriter(responseStream);

                // Process each instance in the study
                foreach (var instance in instances)
                {
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    if (!System.IO.File.Exists(filePath))
                    {
                        DicomLogger.Warning("WADO", "DICOMweb - Instance file not found: {FilePath}", filePath);
                        continue;
                    }

                    try
                    {
                        // Read DICOM file
                        var dicomFile = await DicomFile.OpenAsync(filePath);
                        var currentTransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;

                        // Transcode if needed
                        if (currentTransferSyntax != targetTransferSyntax)
                        {
                            var transcoder = new DicomTranscoder(currentTransferSyntax, targetTransferSyntax);
                            dicomFile = transcoder.Transcode(dicomFile);
                        }

                        // Save DICOM file to temporary stream
                        using var tempStream = new MemoryStream();
                        await dicomFile.SaveAsync(tempStream);
                        var dicomBytes = tempStream.ToArray();

                        // Write boundary and headers
                        await writer.WriteLineAsync($"--{boundary}");
                        await writer.WriteLineAsync("Content-Type: application/dicom");
                        await writer.WriteLineAsync($"Content-Length: {dicomBytes.Length}");
                        await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{instance.SeriesInstanceUid}/instances/{instance.SopInstanceUid}");
                        await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        // Write DICOM data
                        await responseStream.WriteAsync(dicomBytes, 0, dicomBytes.Length);
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        DicomLogger.Debug("WADO", "DICOMweb - Added study instance to response: {SopInstanceUid}, Size: {Size} bytes",
                            instance.SopInstanceUid, dicomBytes.Length);
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("WADO", ex, "DICOMweb - Failed to process study instance: {SopInstanceUid}",
                            instance.SopInstanceUid);
                        continue;
                    }
                }

                // Write end boundary
                await writer.WriteLineAsync($"--{boundary}--");
                await writer.FlushAsync();

                // Prepare response data
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // Set response headers
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                DicomLogger.Information("WADO", "DICOMweb - Returning study data: {StudyUID}, Size: {Size} bytes, TransferSyntax: {TransferSyntax}",
                    studyInstanceUid, responseBytes.Length, targetTransferSyntax.UID.Name);

                return File(responseBytes, $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - Study retrieval failed");
                return StatusCode(500, "Error retrieving study");
            }
        }

        // WADO-RS: Retrieve Series
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}")]
        [Produces("multipart/related", "application/dicom")]
        public async Task<IActionResult> RetrieveSeries(string studyInstanceUid, string seriesInstanceUid)
        {
            try
            {
                // Log request information
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", "DICOMweb - Received series retrieval request - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, Accept: {Accept}",
                    studyInstanceUid, seriesInstanceUid, acceptHeader);

                // Get instances in the specified series
                var instances = await Task.FromResult(_repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
                if (!instances.Any())
                {
                    DicomLogger.Warning("WADO", "DICOMweb - Series not found: {StudyUID}/{SeriesUID}",
                    studyInstanceUid, seriesInstanceUid);
                    return NotFound("Series not found");
                }

                // Sort by instance number
                instances = instances.OrderBy(i => int.Parse(i.InstanceNumber ?? "0")).ToList();

                // Determine transfer syntax
                DicomTransferSyntax targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));

                if (transferSyntaxPart != null)
                {
                    var transferSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                    if (transferSyntax == "*")
                    {
                        // Use the original transfer syntax of the first instance
                        var firstInstance = instances.First();
                        var firstFilePath = Path.Combine(_settings.StoragePath, firstInstance.FilePath);
                        var firstDicomFile = await DicomFile.OpenAsync(firstFilePath);
                        targetTransferSyntax = firstDicomFile.FileMetaInfo.TransferSyntax;
                        DicomLogger.Information("WADO", "DICOMweb - Using original transfer syntax: {TransferSyntax}",
                            targetTransferSyntax.UID.Name);
                    }
                    else
                    {
                        targetTransferSyntax = DicomTransferSyntax.Parse(transferSyntax);
                    }
                }

                // Create new response stream
                using var responseStream = new MemoryStream();
                using var writer = new StreamWriter(responseStream, leaveOpen: true);

                // Write multipart response
                var boundary = $"boundary.{Guid.NewGuid():N}";

                // Process each instance in the series
                foreach (var instance in instances)
                {
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    if (!System.IO.File.Exists(filePath))
                    {
                        DicomLogger.Warning("WADO", "DICOMweb - Instance file not found: {FilePath}", filePath);
                        continue;
                    }

                    try
                    {
                        // Read and process DICOM file
                        var dicomFile = await DicomFile.OpenAsync(filePath);
                        var currentTransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;

                        // Transcode if needed
                        if (currentTransferSyntax != targetTransferSyntax)
                        {
                            var transcoder = new DicomTranscoder(currentTransferSyntax, targetTransferSyntax);
                            dicomFile = transcoder.Transcode(dicomFile);
                        }

                        // Save DICOM file to temporary stream
                        using var tempStream = new MemoryStream();
                        await dicomFile.SaveAsync(tempStream);
                        var dicomBytes = tempStream.ToArray();

                        // Write boundary and headers
                        await writer.WriteLineAsync($"--{boundary}");
                        await writer.WriteLineAsync("Content-Type: application/dicom");
                        await writer.WriteLineAsync($"Content-Length: {dicomBytes.Length}");
                        await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{instance.SopInstanceUid}");
                        await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        // Write DICOM data
                        await responseStream.WriteAsync(dicomBytes, 0, dicomBytes.Length);
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        DicomLogger.Debug("WADO", "DICOMweb - Added series instance to response: {SopInstanceUid}, Size: {Size} bytes",
                            instance.SopInstanceUid, dicomBytes.Length);
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("WADO", ex, "DICOMweb - Failed to process series instance: {SopInstanceUid}",
                            instance.SopInstanceUid);
                        continue;
                    }
                }

                // Write end boundary
                await writer.WriteLineAsync($"--{boundary}--");
                await writer.FlushAsync();

                // Prepare response data
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // Set response headers
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                DicomLogger.Information("WADO", "DICOMweb - Returning series data: {SeriesUID}, Size: {Size} bytes, TransferSyntax: {TransferSyntax}",
                    seriesInstanceUid, responseBytes.Length, targetTransferSyntax.UID.Name);

                return File(responseBytes, $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - Series retrieval failed");
                return StatusCode(500, "Error retrieving series");
            }
        }

        // WADO-RS: Retrieve DICOM Instance
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}")]
        [Produces("multipart/related", "application/dicom")]
        public async Task<IActionResult> RetrieveDicomInstance(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid)
        {
            try
            {
                // Log request information
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", "DICOMweb - Received instance retrieval request - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, SopUID: {SopUID}, Accept: {Accept}",
                    studyInstanceUid, seriesInstanceUid, sopInstanceUid, acceptHeader);

                // Get instance information
                var instances = await Task.FromResult(_repository.GetInstancesByStudyUid(studyInstanceUid));
                var instance = instances.FirstOrDefault(i =>
                    i.SeriesInstanceUid == seriesInstanceUid &&
                    i.SopInstanceUid == sopInstanceUid);

                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "DICOMweb - Instance not found: {SopInstanceUid}", sopInstanceUid);
                    return NotFound("Instance not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOMweb - DICOM file not found: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // Read DICOM file
                var dicomFile = await DicomFile.OpenAsync(filePath);
                var currentTransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;

                // Parse Accept header
                var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                var mediaType = acceptParts.First().Trim();
                var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));
                var typePart = acceptParts.FirstOrDefault(p => p.StartsWith("type=", StringComparison.OrdinalIgnoreCase));

                // Determine media type
                if (mediaType == "*/*" || string.IsNullOrEmpty(mediaType))
                {
                    mediaType = AppDicomContentType;
                }

                // Extract actual media type from multipart/related
                if (mediaType == "multipart/related" && typePart != null)
                {
                    var type = typePart.Split('=')[1].Trim('"', ' ');
                    mediaType = type;
                }

                // Determine transfer syntax
                DicomTransferSyntax targetTransferSyntax;
                if (transferSyntaxPart != null)
                {
                    var requestedSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                    if (requestedSyntax == "*")
                    {
                        // Use original transfer syntax
                        targetTransferSyntax = currentTransferSyntax;
                        DicomLogger.Information("WADO", "DICOMweb - Using original transfer syntax: {TransferSyntax}",
                            currentTransferSyntax.UID.Name);
                    }
                    else
                    {
                        targetTransferSyntax = DicomTransferSyntax.Parse(requestedSyntax);
                        DicomLogger.Information("WADO", "DICOMweb - Using requested transfer syntax: {TransferSyntax}",
                            targetTransferSyntax.UID.Name);
                    }
                }
                else
                {
                    // Default to Explicit VR Little Endian (1.2.840.10008.1.2.1)
                    targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                    DicomLogger.Information("WADO", "DICOMweb - Using default transfer syntax: ExplicitVRLittleEndian");
                }

                // Transcode if needed
                if (currentTransferSyntax != targetTransferSyntax)
                {
                    DicomLogger.Information("WADO",
                    "DICOMweb - Transcoding transfer syntax - From: {CurrentSyntax} To: {TargetSyntax}",
                    currentTransferSyntax.UID.Name,
                    targetTransferSyntax.UID.Name);

                    var transcoder = new DicomTranscoder(currentTransferSyntax, targetTransferSyntax);
                    dicomFile = transcoder.Transcode(dicomFile);
                }

                // Save DICOM file to memory stream
                using var memoryStream = new MemoryStream();
                await dicomFile.SaveAsync(memoryStream);
                var dicomBytes = memoryStream.ToArray();

                // If single DICOM file is requested
                if (mediaType == AppDicomContentType && !acceptHeader.Contains("multipart/related"))
                {
                    Response.Headers["Content-Type"] = AppDicomContentType;
                    Response.Headers["Content-Length"] = dicomBytes.Length.ToString();
                    Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;
                    return File(dicomBytes, AppDicomContentType, $"{sopInstanceUid}.dcm");
                }

                // Default to multipart/related format
                var boundary = $"boundary_{Guid.NewGuid():N}";
                var responseStream = new MemoryStream();
                var writer = new StreamWriter(responseStream, System.Text.Encoding.UTF8);

                // Write first boundary
                await writer.WriteLineAsync($"--{boundary}");

                // Write MIME headers
                await writer.WriteLineAsync("Content-Type: application/dicom");
                await writer.WriteLineAsync($"Content-Length: {dicomBytes.Length}");
                await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}");
                await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                await writer.WriteLineAsync();
                await writer.FlushAsync();

                // Write DICOM data
                await responseStream.WriteAsync(dicomBytes, 0, dicomBytes.Length);

                // Write end boundary
                var endBoundary = $"\r\n--{boundary}--\r\n";
                var endBoundaryBytes = System.Text.Encoding.UTF8.GetBytes(endBoundary);
                await responseStream.WriteAsync(endBoundaryBytes, 0, endBoundaryBytes.Length);

                // Prepare response data
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // Set response headers
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                DicomLogger.Information("WADO",
                    "DICOMweb - Returning DICOM instance: {SopInstanceUid}, Size: {Size} bytes, TransferSyntax: {TransferSyntax}",
                    sopInstanceUid ?? string.Empty, responseBytes.Length, targetTransferSyntax.UID.Name ?? string.Empty);

                return File(responseBytes, $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", "Failed to retrieve DICOM instance: {Error}", ex.Message ?? string.Empty);
                return StatusCode(500, "Error retrieving DICOM instance");
            }
        }

        // WADO-RS: Retrieve Frames
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/frames/{frameNumbers}")]
        [Produces("multipart/related")]
        public async Task<IActionResult> RetrieveFrames(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid,
            string frameNumbers)
        {
            try
            {
                // Log request information
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO",
                    "DICOMweb - Received frame retrieval request - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, SopUID: {SopUID}, Frames: {Frames}, Accept: {Accept}",
                    studyInstanceUid, seriesInstanceUid, sopInstanceUid, frameNumbers, acceptHeader);

                // Validate frame numbers format
                if (!frameNumbers.Split(',').All(f => int.TryParse(f, out int n) && n >= 1))
                {
                    return BadRequest("Invalid frame numbers. Frame numbers must be positive integers.");
                }

                // Get instance
                var instances = await Task.FromResult(_repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
                var instance = instances.FirstOrDefault(i => i.SopInstanceUid == sopInstanceUid);
                if (instance == null)
                {
                    return NotFound("Instance not found");
                }

                // Read DICOM file
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound("DICOM file not found");
                }

                var dicomFile = await DicomFile.OpenAsync(filePath);
                var dataset = dicomFile.Dataset;

                // Modify this logic: if NumberOfFrames does not exist, assume it is a single-frame image
                int numberOfFrames = 1;
                if (dataset.Contains(DicomTag.NumberOfFrames))
                {
                    numberOfFrames = dataset.GetSingleValue<int>(DicomTag.NumberOfFrames);
                }

                var requestedFrames = frameNumbers.Split(',').Select(int.Parse).ToList();

                // Validate frame number range
                if (requestedFrames.Any(f => f < 1 || f > numberOfFrames))
                {
                    return BadRequest($"Frame numbers must be between 1 and {numberOfFrames}");
                }

                // Process Accept header
                var (mediaType, targetTransferSyntax) = GetFrameMediaTypeAndTransferSyntax(
                    acceptHeader,
                    dicomFile.FileMetaInfo.TransferSyntax);

                // Create response
                var boundary = $"boundary.{Guid.NewGuid():N}";
                using var responseStream = new MemoryStream();
                using var writer = new StreamWriter(responseStream, leaveOpen: true);

                // Preprocess dataset
                var pixelData = DicomPixelData.Create(dataset);
                var failedFrames = new List<int>();

                // Process each frame
                foreach (var frameNumber in requestedFrames)
                {
                    try
                    {
                        var frameData = GetFrameData(dataset, frameNumber, mediaType, targetTransferSyntax);

                        // Write boundary and headers
                        await writer.WriteLineAsync($"--{boundary}");
                        await writer.WriteLineAsync($"Content-Type: {mediaType}");
                        await writer.WriteLineAsync($"Content-Length: {frameData.Length}");
                        await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/frames/{frameNumber}");
                        if (mediaType == "application/octet-stream")
                        {
                            await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                        }
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        // Write frame data
                        await responseStream.WriteAsync(frameData, 0, frameData.Length);
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        DicomLogger.Debug("WADO",
                            "DICOMweb - Added frame to response: Frame={FrameNumber}, Size={Size} bytes",
                            frameNumber, frameData.Length);
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("WADO", ex,
                            "DICOMweb - Failed to process frame: Frame={FrameNumber}", frameNumber);
                        failedFrames.Add(frameNumber);
                        continue;
                    }
                }

                // If all frames failed, return error
                if (failedFrames.Count == requestedFrames.Count)
                {
                    return StatusCode(500, "Failed to process all requested frames");
                }

                // Write end boundary
                await writer.WriteLineAsync($"--{boundary}--");
                await writer.FlushAsync();

                // Prepare response data
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // Set response headers
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"{mediaType}\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                if (mediaType == "application/octet-stream")
                {
                    Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;
                }
                if (failedFrames.Any())
                {
                    Response.Headers["Warning"] = $"299 {Request.Host} \"Failed to process frames: {string.Join(",", failedFrames)}\"";
                }

                return File(responseBytes, $"multipart/related; type=\"{mediaType}\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - Frame retrieval failed");
                return StatusCode(500, "Error retrieving frames");
            }
        }

        private byte[] GetFrameData(DicomDataset dataset, int frameNumber, string mediaType, DicomTransferSyntax targetTransferSyntax)
        {
            var pixelData = DicomPixelData.Create(dataset);
            var originalFrameData = pixelData.GetFrame(frameNumber - 1).Data.ToArray();

            // If the requested transfer syntax is the original or the current transfer syntax matches the target, return the original data
            if (targetTransferSyntax == dataset.InternalTransferSyntax ||
            targetTransferSyntax.UID.UID == "*")
            {
                return originalFrameData;
            }

            // Transcode based on media type and transfer syntax
            var transcoder = new DicomTranscoder(
            dataset.InternalTransferSyntax,
            mediaType == "image/jp2" ? DicomTransferSyntax.JPEG2000Lossless : targetTransferSyntax);

            var newDataset = transcoder.Transcode(dataset);
            var newPixelData = DicomPixelData.Create(newDataset);
            return newPixelData.GetFrame(frameNumber - 1).Data.ToArray();
        }

        private (string MediaType, DicomTransferSyntax TransferSyntax) GetFrameMediaTypeAndTransferSyntax(
            string acceptHeader,
            DicomTransferSyntax originalTransferSyntax)
        {
            var mediaType = "application/octet-stream";
            var targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;

            if (string.IsNullOrEmpty(acceptHeader) || acceptHeader == "*/*")
            {
                return (mediaType, targetTransferSyntax);
            }

            var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
            var typePart = acceptParts.FirstOrDefault(p => p.StartsWith("type=", StringComparison.OrdinalIgnoreCase));
            var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));

            // Handle media type
            if (typePart != null)
            {
                var type = typePart.Split('=')[1].Trim('"', ' ');
                if (type == "image/jp2")
                {
                    mediaType = "image/jp2";
                    targetTransferSyntax = DicomTransferSyntax.JPEG2000Lossless;
                }
            }

            // Handle transfer syntax
            if (transferSyntaxPart != null)
            {
                var transferSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                if (transferSyntax == "*")
                {
                    targetTransferSyntax = originalTransferSyntax;
                }
                else
                {
                    targetTransferSyntax = DicomTransferSyntax.Parse(transferSyntax);
                }
            }
            else if (mediaType == "image/jp2")
            {
                targetTransferSyntax = DicomTransferSyntax.JPEG2000Lossless;
            }

            return (mediaType, targetTransferSyntax);
        }

        // WADO-RS: Retrieve Series Metadata
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/metadata")]
        [Produces("application/dicom+json")]
        public async Task<IActionResult> GetSeriesMetadata(string studyInstanceUid, string seriesInstanceUid)
        {
            // Tags to exclude
            var excludedTags = new HashSet<DicomTag>
            {
            // Pixel data related
            DicomTag.PixelData,
            DicomTag.FloatPixelData,
            DicomTag.DoubleFloatPixelData,
            DicomTag.OverlayData,
            DicomTag.EncapsulatedDocument,
            DicomTag.SpectroscopyData
            };

            // Required tags
            var requiredTags = new HashSet<DicomTag>
            {
            // File meta information
            DicomTag.TransferSyntaxUID,
            DicomTag.SpecificCharacterSet,

            // Study level
            DicomTag.StudyInstanceUID,
            DicomTag.StudyDate,
            DicomTag.StudyTime,
            DicomTag.StudyID,
            DicomTag.AccessionNumber,
            DicomTag.StudyDescription,

            // Series level
            DicomTag.SeriesInstanceUID,
            DicomTag.SeriesNumber,
            DicomTag.Modality,
            DicomTag.SeriesDescription,
            DicomTag.SeriesDate,
            DicomTag.SeriesTime,

            // Instance level
            DicomTag.SOPClassUID,
            DicomTag.SOPInstanceUID,
            DicomTag.InstanceNumber,
            DicomTag.ImageType,
            DicomTag.AcquisitionNumber,
            DicomTag.AcquisitionDate,
            DicomTag.AcquisitionTime,
            DicomTag.ContentDate,
            DicomTag.ContentTime,

            // Device information
            DicomTag.Manufacturer,
            DicomTag.ManufacturerModelName,
            DicomTag.StationName,

            // Patient information
            DicomTag.PatientName,
            DicomTag.PatientID,
            DicomTag.PatientBirthDate,
            DicomTag.PatientSex,
            DicomTag.PatientAge,
            DicomTag.PatientWeight,
            DicomTag.PatientPosition,

            // Image related
            DicomTag.Rows,
            DicomTag.Columns,
            DicomTag.BitsAllocated,
            DicomTag.BitsStored,
            DicomTag.HighBit,
            DicomTag.PixelRepresentation,
            DicomTag.SamplesPerPixel,
            DicomTag.PhotometricInterpretation,
            DicomTag.PlanarConfiguration,
            DicomTag.RescaleIntercept,
            DicomTag.RescaleSlope,

            // Spatial information
            DicomTag.ImageOrientationPatient,
            DicomTag.ImagePositionPatient,
            DicomTag.SliceLocation,
            DicomTag.SliceThickness,
            DicomTag.PixelSpacing,
            DicomTag.WindowCenter,
            DicomTag.WindowWidth,
            DicomTag.WindowCenterWidthExplanation,

            // Scanning parameters
            DicomTag.KVP,
            DicomTag.ExposureTime,
            DicomTag.XRayTubeCurrent,
            DicomTag.Exposure,
            DicomTag.ConvolutionKernel,
            DicomTag.PatientOrientation,
            DicomTag.ImageComments,

            // Multi-frame related
            DicomTag.NumberOfFrames,
            DicomTag.FrameIncrementPointer
            };

            try
            {
                // 1. Get all instances of the series from the database
                var instances = _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid);
                if (!instances.Any())
                {
                    return NotFound($"Series not found: {seriesInstanceUid}");
                }

                var metadata = new List<Dictionary<string, object>>();

                // 2. Read DICOM tags for each instance
                foreach (var instance in instances)
                {
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    if (!System.IO.File.Exists(filePath))
                    {
                        DicomLogger.Warning("WADO", "File not found: {Path}", filePath);
                        continue;
                    }

                    try
                    {
                        var dicomFile = await DicomFile.OpenAsync(filePath);
                        var tags = new Dictionary<string, object>();

                        // Ensure required tags exist
                        foreach (var tag in requiredTags)
                        {
                            if (dicomFile.Dataset.Contains(tag))
                            {
                                var tagKey = $"{tag.Group:X4}{tag.Element:X4}";
                                var value = ConvertDicomValueToJson(dicomFile.Dataset.GetDicomItem<DicomItem>(tag));
                                if (value != null)
                                {
                                    tags[tagKey] = value;
                                }
                            }
                        }

                        // Add other tags
                        foreach (var tag in dicomFile.Dataset)
                        {
                            if (excludedTags.Contains(tag.Tag) || tag.Tag.IsPrivate)
                            {
                                continue;
                            }

                            // Skip already processed required tags
                            if (requiredTags.Contains(tag.Tag))
                            {
                                continue;
                            }

                            var tagKey = $"{tag.Tag.Group:X4}{tag.Tag.Element:X4}";
                            var value = ConvertDicomValueToJson(tag);
                            if (value != null)
                            {
                                tags[tagKey] = value;
                            }
                        }

                        metadata.Add(tags);
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("WADO", ex, "Failed to read DICOM file: {Path}", filePath);
                    }
                }

                return Ok(metadata);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "Failed to get series metadata - Study: {Study}, Series: {Series}",
                    studyInstanceUid, seriesInstanceUid);
                return StatusCode(500, "Failed to get series metadata");
            }
        }

        private object? ConvertDicomValueToJson(DicomItem item)
        {
            try
            {
                // DICOMweb JSON format requirements
                var result = new Dictionary<string, object?>
            {
                { "vr", item.ValueRepresentation.Code }
            };

                // Handle value based on VR type
                if (item is DicomElement element)
                {
                    switch (item.ValueRepresentation.Code)
                    {
                        case "SQ":
                            if (item is DicomSequence sq)
                            {
                                var items = new List<Dictionary<string, object>>();
                                foreach (var dataset in sq.Items)
                                {
                                    var seqItem = new Dictionary<string, object>();
                                    foreach (var tag in dataset)
                                    {
                                        var tagKey = $"{tag.Tag.Group:X4}{tag.Tag.Element:X4}";
                                        var value = ConvertDicomValueToJson(tag);
                                        if (value != null)
                                        {
                                            seqItem[tagKey] = value;
                                        }
                                    }
                                    items.Add(seqItem);
                                }
                                if (items.Any())
                                {
                                    result["Value"] = items;
                                }
                            }
                            break;

                        case "PN":
                            var personNames = element.Get<string[]>();
                            result["Value"] = personNames?.Select(pn => new Dictionary<string, string>
                    {
                    { "Alphabetic", pn }
                    }).ToArray() ?? Array.Empty<Dictionary<string, string>>();
                            break;

                        case "DA":
                            var dates = element.Get<string[]>();
                            if (dates?.Any() == true)
                            {
                                result["Value"] = dates.Select(x => x?.Replace("-", "")).ToArray();
                            }
                            break;

                        case "TM":
                            var times = element.Get<string[]>();
                            if (times?.Any() == true)
                            {
                                result["Value"] = times.Select(x => x?.Replace(":", "")).ToArray();
                            }
                            break;

                        case "AT":
                            var tags = element.Get<DicomTag[]>();
                            if (tags?.Any() == true)
                            {
                                result["Value"] = tags.Select(t => $"{t.Group:X4}{t.Element:X4}").ToArray();
                            }
                            break;

                        default:
                            if (element.Count > 0)
                            {
                                var values = element.Get<string[]>();
                                if (values?.Any() == true)
                                {
                                    result["Value"] = values;
                                }
                                else
                                {
                                    result["Value"] = Array.Empty<string>();
                                }
                            }
                            else
                            {
                                result["Value"] = Array.Empty<string>();
                            }
                            break;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "Failed to convert DICOM tag: {Tag}", item.Tag);
                return null;
            }
        }

        #endregion

        #region WADO-RS Thumbnail Interfaces

        // WADO-RS: Retrieve Series Thumbnail
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/thumbnail")]
        [Produces(JpegImageContentType)]
        public async Task<IActionResult> RetrieveSeriesThumbnail(
            string studyInstanceUid,
            string seriesInstanceUid,
            [FromQuery] int? size = null,
            [FromQuery] string? viewport = null)
        {
            try
            {
                // Parse size from viewport parameter (format: width,height)
                if (viewport != null && size == null)
                {
                    var dimensions = viewport.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (dimensions.Length > 0 && int.TryParse(dimensions[0].Trim('%'), out int width))
                    {
                        size = width;
                        DicomLogger.Debug("WADO", "DICOMweb - Using viewport parameter: {SeriesInstanceUid}, Viewport: {Viewport}, Parsed size: {Size}",
                            seriesInstanceUid, viewport, size);
                    }
                }
                else if (viewport != null && size != null)
                {
                    DicomLogger.Debug("WADO", "DICOMweb - Both size and viewport parameters provided, using size: {Size}, ignoring viewport: {Viewport}",
                    size, viewport);
                }
                else if (size != null)
                {
                    DicomLogger.Debug("WADO", "DICOMweb - Using size parameter: {Size}", size);
                }
                else
                {
                    DicomLogger.Debug("WADO", "DICOMweb - Using default size: 128");
                }

                // If neither size nor viewport is provided, use default value
                var thumbnailSize = size ?? 128;

                // Get the first instance in the series
                var instances = _repository.GetInstancesByStudyUid(studyInstanceUid);
                var instance = instances.FirstOrDefault(i =>
                    i.SeriesInstanceUid == seriesInstanceUid);

                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "DICOMweb - Series not found: {SeriesInstanceUid}", seriesInstanceUid);
                    return NotFound("Series not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOMweb - DICOM file not found: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // Read DICOM file
                var dicomFile = await DicomFile.OpenAsync(filePath);
                DicomLogger.Debug("WADO", "DICOMweb - Generating series thumbnail: {SeriesInstanceUid}, Size: {Size}",
                    seriesInstanceUid, thumbnailSize);
                var dicomImage = new DicomImage(dicomFile.Dataset);
                var renderedImage = dicomImage.RenderImage();

                // Convert to JPEG thumbnail
                byte[] jpegBytes;
                using (var memoryStream = new MemoryStream())
                {
                    using var image = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                    renderedImage.AsBytes(),
                    renderedImage.Width,
                    renderedImage.Height);

                    // Calculate thumbnail size, maintaining aspect ratio
                    var ratio = Math.Min((double)thumbnailSize / image.Width, (double)thumbnailSize / image.Height);
                    var newWidth = (int)(image.Width * ratio);
                    var newHeight = (int)(image.Height * ratio);

                    DicomLogger.Debug("WADO", "DICOMweb - Series thumbnail size: {SeriesInstanceUid}, Original: {OriginalWidth}x{OriginalHeight}, New: {NewWidth}x{NewHeight}",
                    seriesInstanceUid, image.Width, image.Height, newWidth, newHeight);

                    // Resize image
                    image.Mutate(x => x.Resize(newWidth, newHeight));

                    // Configure JPEG encoder options - use lower quality for thumbnails to reduce file size
                    var encoder = new JpegEncoder
                    {
                        Quality = 75  // Lower quality for thumbnails
                    };

                    // Save as JPEG
                    await image.SaveAsJpegAsync(memoryStream, encoder);
                    jpegBytes = memoryStream.ToArray();
                }

                DicomLogger.Debug("WADO", "DICOMweb - Returning series thumbnail: {SeriesInstanceUid}, Size: {Size} bytes",
                    seriesInstanceUid, jpegBytes.Length);

                return File(jpegBytes, JpegImageContentType, $"{seriesInstanceUid}_thumbnail.jpg");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - Failed to retrieve series thumbnail");
                return StatusCode(500, "Error retrieving series thumbnail");
            }
        }

        #endregion
    }
}
