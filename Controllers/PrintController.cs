using Microsoft.AspNetCore.Mvc;
using DicomSCP.Data;
using DicomSCP.Models;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using DicomSCP.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrintController : Controller
{
    private readonly DicomRepository _repository;
    private readonly ILogger<PrintController> _logger;
    private readonly IWebHostEnvironment _environment;
    private readonly DicomSettings _settings;

    public PrintController(
        DicomRepository repository,
        ILogger<PrintController> logger,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _repository = repository;
        _logger = logger;
        _environment = environment;
        _settings = configuration.GetSection("DicomSettings").Get<DicomSettings>()
            ?? throw new InvalidOperationException("DicomSettings configuration is missing");
    }

    [HttpGet]
    public async Task<IActionResult> GetPrintJobs(
        [FromQuery] string? callingAE = null,
        [FromQuery] string? studyUID = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTime? date = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        try
        {
            var result = await _repository.GetPrintJobsAsync(callingAE, studyUID, status, date, page, pageSize);
            return Ok(new
            {
                items = result.Items,
                total = result.Total,
                page = result.Page,
                pageSize = result.PageSize,
                totalPages = result.TotalPages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching print jobs");
            return StatusCode(500, new { message = "Failed to fetch print jobs" });
        }
    }

    [HttpGet("{jobId}")]
    public async Task<IActionResult> GetPrintJob(string jobId)
    {
        try
        {
            var job = await _repository.GetPrintJobAsync(jobId);
            if (job == null)
            {
                return NotFound(new { message = "Print job not found" });
            }

            // Convert to view model
            var jobView = new
            {
                job.JobId,
                job.FilmSessionId,
                job.FilmBoxId,
                job.CallingAE,
                job.Status,
                job.ErrorMessage,
                // Film Session parameters
                job.NumberOfCopies,
                job.PrintPriority,
                job.MediumType,
                job.FilmDestination,
                // Film Box parameters
                job.PrintInColor,
                job.FilmOrientation,
                job.FilmSizeID,
                job.ImageDisplayFormat,
                job.MagnificationType,
                job.SmoothingType,
                job.BorderDensity,
                job.EmptyImageDensity,
                job.Trim,
                // Image information
                job.ImagePath,
                // Study information
                job.StudyInstanceUID,
                // Timestamps
                job.CreateTime,
                job.UpdateTime,
                // Add image URL
                ImageUrl = !string.IsNullOrEmpty(job.ImagePath) ?
                    $"/api/print/{job.JobId}/image" : null
            };

            return Ok(jobView);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching print job details");
            return StatusCode(500, new { message = "Failed to fetch print job details" });
        }
    }

    [HttpDelete("{jobId}")]
    public async Task<IActionResult> DeletePrintJob(string jobId)
    {
        try
        {
            // First, get the job information
            var job = await _repository.GetPrintJobAsync(jobId);
            if (job == null)
            {
                return NotFound(new { message = "Print job not found" });
            }

            // If there is an image file, delete the image
            if (!string.IsNullOrEmpty(job.ImagePath))
            {
                var fullPath = GetImageFullPath(job);
                if (System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                }
            }

            // Delete the database record
            var result = await _repository.DeletePrintJobAsync(jobId);
            return Ok(new { message = "Deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while deleting print job");
            return StatusCode(500, new { message = "Failed to delete print job" });
        }
    }

    [HttpGet("{jobId}/image")]
    public async Task<IActionResult> GetPrintJobImage(string jobId, [FromQuery] int? width = null, [FromQuery] int? height = null)
    {
        try
        {
            // Validate parameters
            if (width.HasValue && width.Value <= 0)
            {
                return BadRequest(new { message = "Width must be greater than 0" });
            }
            if (height.HasValue && height.Value <= 0)
            {
                return BadRequest(new { message = "Height must be greater than 0" });
            }

            // Get the print job
            var job = await GetPrintJobAsync(jobId);
            if (job == null)
            {
                return NotFound(new { message = "Print job not found" });
            }

            // Get the image file path
            var fullPath = GetImageFullPath(job);
            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound(new { message = $"Image file for print job not found: {job.ImagePath}" });
            }

            // Convert the image and return, supporting specified dimensions
            return await ConvertDicomToImageAsync(fullPath, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching print job image: {Message}", ex.Message);
            return StatusCode(500, new { message = "Failed to fetch print job image" });
        }
    }

    private async Task<PrintJob?> GetPrintJobAsync(string jobId)
    {
        var job = await _repository.GetPrintJobAsync(jobId);
        if (job == null || string.IsNullOrEmpty(job.ImagePath))
        {
            return null;
        }
        return job;
    }

    private string GetImageFullPath(PrintJob job)
    {
        var storagePath = _settings.StoragePath ?? "received_files";
        if (!Path.IsPathRooted(storagePath))
        {
            storagePath = Path.Combine(_environment.ContentRootPath, storagePath);
        }
        return Path.Combine(storagePath, job.ImagePath);
    }

    private async Task<IActionResult> ConvertDicomToImageAsync(string dicomFilePath, int? width = null, int? height = null)
    {
        // Read the DICOM file
        var dicomFile = await DicomFile.OpenAsync(dicomFilePath);
        var dicomImage = new DicomImage(dicomFile.Dataset);

        // Get the original image data
        var image = dicomImage.RenderImage();

        // Calculate the target size
        var (targetWidth, targetHeight) = CalculateTargetSize(
            originalWidth: image.Width,
            originalHeight: image.Height,
            requestedWidth: width,
            requestedHeight: height);

        using var memoryStream = new MemoryStream();
        using (var outputImage = Image.LoadPixelData<Rgba32>(
            image.AsBytes(),
            image.Width,
            image.Height))
        {
            // Resize the image
            if (targetWidth != image.Width || targetHeight != image.Height)
            {
                outputImage.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(targetWidth, targetHeight),
                    Mode = ResizeMode.Max,  // Maintain aspect ratio
                    Sampler = KnownResamplers.Lanczos3  // Use Lanczos algorithm for better quality
                }));
            }

            // Configure PNG encoder options to optimize output size
            var encoder = new PngEncoder
            {
                CompressionLevel = PngCompressionLevel.BestSpeed,  // Use the fastest compression method
                FilterMethod = PngFilterMethod.None,  // No filtering to improve performance
                ColorType = PngColorType.Rgb  // Use RGB format without alpha channel
            };

            // Save as PNG
            await outputImage.SaveAsPngAsync(memoryStream, encoder);
        }

        memoryStream.Position = 0;
        return new FileContentResult(memoryStream.ToArray(), "image/png");
    }

    /// <summary>
    /// Calculate the target image size
    /// </summary>
    private (int width, int height) CalculateTargetSize(
        int originalWidth,
        int originalHeight,
        int? requestedWidth,
        int? requestedHeight)
    {
        // If no dimensions are specified, return the original size
        if (!requestedWidth.HasValue && !requestedHeight.HasValue)
        {
            return (originalWidth, originalHeight);
        }

        float originalRatio = (float)originalWidth / originalHeight;

        // Both width and height specified
        if (requestedWidth.HasValue && requestedHeight.HasValue)
        {
            return (requestedWidth.Value, requestedHeight.Value);
        }

        // Only width specified
        if (requestedWidth.HasValue)
        {
            int height = (int)(requestedWidth.Value / originalRatio);
            return (requestedWidth.Value, height);
        }

        // Only height specified
        int width = (int)(requestedHeight!.Value * originalRatio);
        return (width, requestedHeight.Value);
    }
}
