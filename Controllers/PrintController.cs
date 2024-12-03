using Microsoft.AspNetCore.Mvc;
using DicomSCP.Data;
using DicomSCP.Models;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using DicomSCP.Configuration;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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
            var job = await _repository.GetPrintJobAsync(jobId);
            if (job == null)
            {
                return NotFound(new { message = "打印任务不存在" });
            }

            if (string.IsNullOrEmpty(job.ImagePath))
            {
                return NotFound(new { message = "打印任务的图像路径不存在" });
            }

            // 构建完整的物理路径，使用配置的StoragePath
            var storagePath = _settings.StoragePath ?? "received_files";
            if (!Path.IsPathRooted(storagePath))
            {
                storagePath = Path.Combine(_environment.ContentRootPath, storagePath);
            }
            var fullPath = Path.Combine(storagePath, job.ImagePath);

            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound(new { message = $"打印任务的图像文件不存在: {fullPath}" });
            }

            // 读取DICOM文件
            var dicomFile = await DicomFile.OpenAsync(fullPath);
            var dicomImage = new DicomImage(dicomFile.Dataset);

            // 设置图像渲染参数
            var image = dicomImage.RenderImage();
            
            // 获取图像参数
            int width = image.Width;
            int height = image.Height;

            // 获取像素数据
            var pixelData = image.AsBytes();

            // 创建新的内存流
            using var memoryStream = new MemoryStream();
            using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                // 锁定位图数据
                var bitmapData = bitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);

                try
                {
                    // 复制像素数据
                    System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, bitmapData.Scan0, pixelData.Length);
                }
                finally
                {
                    // 解锁位图
                    bitmap.UnlockBits(bitmapData);
                }

                // 保存为PNG
                bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
            }

            memoryStream.Position = 0;
            // 返回PNG图像
            return File(memoryStream.ToArray(), "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取打印任务图像时发生错误: {Message}", ex.Message);
            return StatusCode(500, new { message = "获取打印任务图像失败" });
        }
    }
} 