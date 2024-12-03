using Microsoft.AspNetCore.Mvc;
using DicomSCP.Data;
using DicomSCP.Models;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using DicomSCP.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
    public async Task<IActionResult> GetPrintJobImage(string jobId)
    {
        try
        {
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

            // 转换图像并返回
            return await ConvertDicomToImageAsync(fullPath);
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

    private async Task<IActionResult> ConvertDicomToImageAsync(string dicomFilePath)
    {
        // 读取DICOM文件
        var dicomFile = await DicomFile.OpenAsync(dicomFilePath);
        var dicomImage = new DicomImage(dicomFile.Dataset);

        // 设置图像渲染参数并获取图像数据
        var image = dicomImage.RenderImage();
        var pixelData = image.AsBytes();

        // 使用ImageSharp创建图像
        using var memoryStream = new MemoryStream();
        using (var outputImage = new Image<Rgba32>(image.Width, image.Height))
        {
            // 复制像素数据
            var stride = image.Width * 4; // 4 bytes per pixel (RGBA)
            for (int y = 0; y < image.Height; y++)
            {
                var rowStart = y * stride;
                for (int x = 0; x < image.Width; x++)
                {
                    var offset = rowStart + (x * 4);
                    outputImage[x, y] = new Rgba32(
                        pixelData[offset],     // R
                        pixelData[offset + 1], // G
                        pixelData[offset + 2], // B
                        pixelData[offset + 3]  // A
                    );
                }
            }

            // 保存为PNG
            await outputImage.SaveAsPngAsync(memoryStream);
        }

        memoryStream.Position = 0;
        return new FileContentResult(memoryStream.ToArray(), "image/png");
    }
} 