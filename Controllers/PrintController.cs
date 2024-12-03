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
    public async Task<IActionResult> GetPrintJobs()
    {
        try
        {
            var items = await _repository.GetPrintJobsAsync();
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取打印任务时发生错误");
            return StatusCode(500, new { message = "获取打印任务失败" });
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
                return NotFound(new { message = "打印任务不存在" });
            }
            return Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取打印任务详情时发生错误");
            return StatusCode(500, new { message = "获取打印任务详情失败" });
        }
    }

    [HttpDelete("{jobId}")]
    public async Task<IActionResult> DeletePrintJob(string jobId)
    {
        try
        {
            var result = await _repository.DeletePrintJobAsync(jobId);
            if (!result)
            {
                return NotFound(new { message = "打印任务不存在" });
            }
            return Ok(new { message = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除打印任务时发生错误");
            return StatusCode(500, new { message = "删除打印任务失败" });
        }
    }

    [HttpGet("{jobId}/image")]
    public async Task<IActionResult> GetPrintJobImage(string jobId, [FromQuery] int? width = null, [FromQuery] int? height = null)
    {
        try
        {
            // 参数验证
            if (width.HasValue && width.Value <= 0)
            {
                return BadRequest(new { message = "宽度必须大于0" });
            }
            if (height.HasValue && height.Value <= 0)
            {
                return BadRequest(new { message = "高度必须大于0" });
            }

            // 获取打印任务
            var job = await GetPrintJobAsync(jobId);
            if (job == null)
            {
                return NotFound(new { message = "打印任务不存在" });
            }

            // 获取图像文件路径
            var fullPath = GetImageFullPath(job);
            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound(new { message = $"打印任务的图像文件不存在: {job.ImagePath}" });
            }

            // 转换图像并返回，支持指定尺寸
            return await ConvertDicomToImageAsync(fullPath, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取打印任务图像时发生错误: {Message}", ex.Message);
            return StatusCode(500, new { message = "获取打印任务图像失败" });
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
        // 读取DICOM文件
        var dicomFile = await DicomFile.OpenAsync(dicomFilePath);
        var dicomImage = new DicomImage(dicomFile.Dataset);
        
        // 获取原始图像数据
        var image = dicomImage.RenderImage();
        
        // 计算目标尺寸
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
            // 调整图像大小
            if (targetWidth != image.Width || targetHeight != image.Height)
            {
                outputImage.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(targetWidth, targetHeight),
                    Mode = ResizeMode.Max,  // 保持宽高比
                    Sampler = KnownResamplers.Lanczos3  // 使用Lanczos算法提供更好的质量
                }));
            }

            // 配置PNG编码器选项，优化输出大小
            var encoder = new PngEncoder
            {
                CompressionLevel = PngCompressionLevel.BestSpeed,  // 使用最快的压缩方式
                FilterMethod = PngFilterMethod.None,  // 不使用过滤，提高性能
                ColorType = PngColorType.Rgb  // 使用RGB格式，不包含Alpha通道
            };

            // 保存为PNG
            await outputImage.SaveAsPngAsync(memoryStream, encoder);
        }

        memoryStream.Position = 0;
        return new FileContentResult(memoryStream.ToArray(), "image/png");
    }

    /// <summary>
    /// 计算目标图像尺寸
    /// </summary>
    private (int width, int height) CalculateTargetSize(
        int originalWidth, 
        int originalHeight,
        int? requestedWidth,
        int? requestedHeight)
    {
        // 如果没有指定任何尺寸，返回原始尺寸
        if (!requestedWidth.HasValue && !requestedHeight.HasValue)
        {
            return (originalWidth, originalHeight);
        }

        float originalRatio = (float)originalWidth / originalHeight;

        // 同时指定宽度和高度
        if (requestedWidth.HasValue && requestedHeight.HasValue)
        {
            return (requestedWidth.Value, requestedHeight.Value);
        }

        // 只指定宽度
        if (requestedWidth.HasValue)
        {
            int height = (int)(requestedWidth.Value / originalRatio);
            return (requestedWidth.Value, height);
        }

        // 只指定高度
        int width = (int)(requestedHeight!.Value * originalRatio);
        return (width, requestedHeight.Value);
    }
} 