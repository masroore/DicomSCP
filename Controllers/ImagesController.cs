using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Data;
using DicomSCP.Services;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly DicomRepository _repository;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public ImagesController(
        DicomRepository repository,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _repository = repository;
        _configuration = configuration;
        _environment = environment;

        // Validate necessary configuration
        var storagePath = configuration["DicomSettings:StoragePath"];
        if (string.IsNullOrEmpty(storagePath))
        {
            throw new InvalidOperationException("DicomSettings:StoragePath must be configured in appsettings.json");
        }
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<StudyInfo>>> GetStudies(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? patientId = null,
        [FromQuery] string? patientName = null,
        [FromQuery] string? accessionNumber = null,
        [FromQuery] string? modality = null,
        [FromQuery] string? studyDate = null)
    {
        try
        {
            // Parse date
            DateTime? searchDate = null;
            if (!string.IsNullOrEmpty(studyDate))
            {
                if (DateTime.TryParse(studyDate, out DateTime date))
                {
                    searchDate = date;
                }
            }

            var result = await _repository.GetStudiesAsync(
                page,
                pageSize,
                patientId,
                patientName,
                accessionNumber,
                modality,
                searchDate,    // Start time
                searchDate?.AddDays(1).AddSeconds(-1)  // End time set to the last second of the day
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] Failed to retrieve study list");
            return StatusCode(500, "Failed to retrieve data");
        }
    }

    [HttpGet("{studyUid}/series")]
    public async Task<ActionResult<IEnumerable<SeriesInfo>>> GetSeries(string studyUid)
    {
        try
        {
            var seriesList = await _repository.GetSeriesByStudyUidAsync(studyUid);
            var result = seriesList.Select(series => new SeriesInfo
            {
                SeriesInstanceUid = series.SeriesInstanceUid,
                SeriesNumber = series.SeriesNumber ?? "",
                Modality = series.StudyModality ?? series.Modality ?? "",
                SeriesDescription = series.SeriesDescription ?? "",
                NumberOfInstances = series.NumberOfInstances
            });

            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] Failed to retrieve series list - StudyUid: {StudyUid}", studyUid);
            return StatusCode(500, "Failed to retrieve data");
        }
    }

    [HttpDelete("{studyInstanceUid}")]
    public async Task<IActionResult> Delete(string studyInstanceUid)
    {
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            return BadRequest("StudyInstanceUID is required");
        }

        try
        {
            // 1. Retrieve study information
            var study = await _repository.GetStudyAsync(studyInstanceUid);
            if (study == null)
            {
                return NotFound("Study not found");
            }

            // 2. Delete files from the file system
            var storagePath = _configuration["DicomSettings:StoragePath"]
                ?? throw new InvalidOperationException("DicomSettings:StoragePath is not configured");

            // Construct study directory path, handle null StudyDate
            var studyPath = string.IsNullOrEmpty(study.StudyDate)
                ? Path.Combine(storagePath, studyInstanceUid)  // If no date, use study UID directly
                : Path.Combine(storagePath, study.StudyDate.Substring(0, 4),
                    study.StudyDate.Substring(4, 2),
                    study.StudyDate.Substring(6, 2),
                    studyInstanceUid);  // Organize by year/month/day/study UID

            if (Directory.Exists(studyPath))
            {
                try
                {
                    // Recursively delete directory and its contents
                    Directory.Delete(studyPath, true);
                    DicomLogger.Information("Api", "Successfully deleted study directory - Path: {Path}", studyPath);

                    // 3. Delete database record
                    await _repository.DeleteStudyAsync(studyInstanceUid);

                    return Ok(new { message = "Successfully deleted" });
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("Api", ex, "Failed to delete study directory - Path: {Path}", studyPath);
                    return StatusCode(500, new { error = "Failed to delete files, please try again" });
                }
            }
            else
            {
                // If directory does not exist, only delete database record
                await _repository.DeleteStudyAsync(studyInstanceUid);
                DicomLogger.Warning("Api", "Study directory does not exist - Path: {Path}", studyPath);
                return Ok(new { message = "Successfully deleted" });
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] Failed to delete study - StudyUID: {StudyUID}", studyInstanceUid);
            return StatusCode(500, new { error = "Failed to delete, please try again" });
        }
    }

    [HttpGet("{studyUid}/series/{seriesUid}/instances")]
    public async Task<ActionResult<IEnumerable<object>>> GetSeriesInstances(string studyUid, string seriesUid)
    {
        try
        {
            var instances = await _repository.GetSeriesInstancesAsync(seriesUid);
            var result = instances.Select(instance => new
            {
                sopInstanceUid = instance.SopInstanceUid,
                instanceNumber = instance.InstanceNumber
            });

            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] Failed to retrieve series instances");
            return StatusCode(500, "Failed to retrieve series instances");
        }
    }

    private string GetStoragePath(string configPath)
    {
        // If it is an absolute path, return it directly
        if (Path.IsPathRooted(configPath))
        {
            return configPath;
        }

        // Use ContentRootPath as the base path
        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configPath));
    }

    [HttpGet("download/{instanceUid}")]
    public async Task<IActionResult> DownloadInstance(string instanceUid, [FromQuery] string? transferSyntax)
    {
        try
        {
            var instance = await _repository.GetInstanceAsync(instanceUid);
            if (instance == null)
            {
                DicomLogger.Warning("Api", "[API] Instance not found - InstanceUid: {InstanceUid}", instanceUid);
                return NotFound("Instance not found");
            }

            // Get storage root path from configuration
            var configPath = _configuration["DicomSettings:StoragePath"]
                ?? throw new InvalidOperationException("DicomSettings:StoragePath is not configured");

            // Handle storage path
            var storagePath = GetStoragePath(configPath);
            DicomLogger.Debug("Api", "Storage path resolved - Config path: {ConfigPath}, Actual path: {StoragePath}",
                configPath, storagePath);

            // Concatenate full file path and normalize
            var fullPath = Path.GetFullPath(Path.Combine(storagePath, instance.FilePath));

            // Add path security check
            if (!fullPath.StartsWith(storagePath))
            {
                DicomLogger.Error("Api", null,
                    "[API] Illegal file path - InstanceUid: {InstanceUid}, StoragePath: {StoragePath}, FullPath: {FullPath}",
                    instanceUid,
                    storagePath,
                    fullPath);
                return BadRequest("Illegal file path");
            }

            if (!System.IO.File.Exists(fullPath))
            {
                DicomLogger.Error("Api", null,
                    "[API] File not found - InstanceUid: {InstanceUid}, StoragePath: {StoragePath}, FullPath: {FullPath}",
                    instanceUid,
                    storagePath,
                    fullPath);
                return NotFound("Image file not found");
            }

            // Read DICOM file
            var file = await DicomFile.OpenAsync(fullPath);

            // If transfer syntax is specified, transcode
            if (!string.IsNullOrEmpty(transferSyntax))
            {
                var currentSyntax = file.Dataset.InternalTransferSyntax;
                var requestedSyntax = GetRequestedTransferSyntax(transferSyntax);

                if (currentSyntax != requestedSyntax)
                {
                    try
                    {
                        DicomLogger.Information("Api",
                            "[API] Starting transcoding - InstanceUid: {InstanceUid}, Original: {Original}, Target: {Target}",
                            instanceUid,
                            currentSyntax.UID.Name,
                            requestedSyntax.UID.Name);

                        var transcoder = new DicomTranscoder(currentSyntax, requestedSyntax);
                        file = transcoder.Transcode(file);

                        DicomLogger.Information("Api", "[API] Transcoding completed");
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("Api", ex,
                            "[API] Transcoding failed - InstanceUid: {InstanceUid}, Original: {Original}, Target: {Target}",
                            instanceUid,
                            currentSyntax.UID.Name,
                            requestedSyntax.UID.Name);
                        // Use original file if transcoding fails
                    }
                }
            }

            // Construct file name
            var fileName = $"{instance.SopInstanceUid}.dcm";

            // Prepare memory stream
            var memoryStream = new MemoryStream();
            await file.SaveAsync(memoryStream);
            memoryStream.Position = 0;

            // Set response headers
            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            Response.Headers.Append("X-Transfer-Syntax", file.Dataset.InternalTransferSyntax.UID.Name);

            return File(memoryStream, "application/dicom");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] Failed to download file - InstanceUid: {InstanceUid}", instanceUid);
            return StatusCode(500, "Failed to download file");
        }
    }

    private DicomTransferSyntax GetRequestedTransferSyntax(string syntax)
    {
        return syntax.ToLower() switch
        {
            "jpeg" => DicomTransferSyntax.JPEGProcess14SV1,
            "jpeg2000" => DicomTransferSyntax.JPEG2000Lossless,
            "jpegls" => DicomTransferSyntax.JPEGLSLossless,
            "rle" => DicomTransferSyntax.RLELossless,
            "explicit" => DicomTransferSyntax.ExplicitVRLittleEndian,
            "implicit" => DicomTransferSyntax.ImplicitVRLittleEndian,
            _ => DicomTransferSyntax.ExplicitVRLittleEndian
        };
    }
}
