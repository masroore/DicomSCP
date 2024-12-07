using Microsoft.AspNetCore.Mvc;
using DicomSCP.Services;
using DicomSCP.Models;
using DicomSCP.Configuration;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/print-scu")]
public class PrintScuController : ControllerBase
{
    private readonly IPrintSCU _printSCU;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PrintScuController> _logger;

    public PrintScuController(
        IPrintSCU printSCU,
        IConfiguration configuration,
        ILogger<PrintScuController> logger)
    {
        _printSCU = printSCU;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 获取打印机列表
    /// </summary>
    [HttpGet("printers")]
    public ActionResult<IEnumerable<PrinterConfig>> GetPrinters()
    {
        var printers = _configuration.GetSection("DicomSettings:PrintSCU:Printers")
            .Get<List<PrinterConfig>>() ?? new List<PrinterConfig>();
        return Ok(printers);
    }

    /// <summary>
    /// 发送打印请求
    /// </summary>
    [HttpPost("print")]
    public async Task<ActionResult> Print([FromBody] PrintRequest request)
    {
        try
        {
            // 验证请求
            if (string.IsNullOrEmpty(request.FilePath))
                return BadRequest("文件路径不能为空");

            if (!System.IO.File.Exists(request.FilePath))
                return BadRequest("文件不存在");

            // 获取打印机配置
            var printers = _configuration.GetSection("DicomSettings:PrintSCU:Printers")
                .Get<List<PrinterConfig>>() ?? new List<PrinterConfig>();
            
            var printer = printers.FirstOrDefault(p => p.Name == request.CalledAE) ?? 
                         printers.FirstOrDefault(p => p.IsDefault);

            if (printer == null)
                return BadRequest("未找到可用的打印机");

            // 获取 PrintSCU 配置
            var printScuConfig = _configuration.GetSection("DicomSettings:PrintSCU").Get<PrintScuConfig>();
            if (printScuConfig == null)
                return StatusCode(500, "PrintSCU 配置未找到");

            // 设置打印请求参数
            request.CalledAE = printer.AeTitle;
            request.HostName = printer.HostName;
            request.Port = printer.Port;
            request.CallingAE = printScuConfig.AeTitle;

            // 发送打印请求
            var success = await _printSCU.PrintAsync(request);

            if (success)
                return Ok(new { message = "打印请求已发送" });

            return StatusCode(500, new { message = "打印失败" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理打印请求时发生错误");
            return StatusCode(500, new { message = "打印失败", error = ex.Message });
        }
    }

    /// <summary>
    /// 测试打印机连接
    /// </summary>
    [HttpPost("printers/{name}/test")]
    public async Task<ActionResult> TestPrinter(string name)
    {
        try
        {
            var printers = _configuration.GetSection("DicomSettings:PrintSCU:Printers")
                .Get<List<PrinterConfig>>() ?? new List<PrinterConfig>();
            
            var printer = printers.FirstOrDefault(p => p.Name == name);
            if (printer == null)
                return NotFound($"打印机 '{name}' 未找到");

            var success = await _printSCU.VerifyAsync(
                printer.HostName,
                printer.Port,
                printer.AeTitle);

            if (success)
                return Ok(new { 
                    success = true,
                    message = "打印机连接测试成功" 
                });

            return Ok(new { 
                success = false,
                message = "打印机连接被拒绝，请检查打印机配置和状态" 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "测试打印机连接时发生错误");
            return Ok(new { 
                success = false,
                message = "连接打印机失败，请检查网络连接" 
            });
        }
    }
} 