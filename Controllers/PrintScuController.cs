using Microsoft.AspNetCore.Mvc;
using DicomSCP.Services;
using DicomSCP.Models;

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
    /// <remarks>
    /// 请求示例:
    /// ```json
    /// {
    ///     "filePath": "D:/dicom/image.dcm",
    ///     "numberOfCopies": 1,
    ///     "printPriority": "MEDIUM",
    ///     "mediumType": "PAPER",
    ///     "filmDestination": "PROCESSOR",
    ///     "filmOrientation": "PORTRAIT",
    ///     "filmSizeID": "14INX17IN",
    ///     "imageDisplayFormat": "STANDARD\\1,1",
    ///     "magnificationType": "REPLICATE",
    ///     "smoothingType": "MEDIUM",
    ///     "borderDensity": "BLACK",
    ///     "emptyImageDensity": "BLACK",
    ///     "trim": "NO"
    /// }
    /// ```
    /// 
    /// 有效值说明：
    /// - printPriority: "HIGH", "MEDIUM", "LOW"
    /// - mediumType: "PAPER", "CLEAR FILM", "BLUE FILM"
    /// - filmDestination: "MAGAZINE", "PROCESSOR", "BIN_1", "BIN_2"
    /// - filmOrientation: "PORTRAIT", "LANDSCAPE"
    /// - filmSizeID: "8INX10IN", "10INX12IN", "11INX14IN", "14INX14IN", "14INX17IN", "24CMX30CM", "A4"
    /// - magnificationType: "REPLICATE", "BILINEAR", "CUBIC", "NONE"
    /// - smoothingType: "NONE", "LOW", "MEDIUM", "HIGH"
    /// - borderDensity: "BLACK", "WHITE"
    /// - emptyImageDensity: "BLACK", "WHITE"
    /// - trim: "YES", "NO"
    /// </remarks>
    [HttpPost("print")]
    public async Task<ActionResult> Print([FromBody] PrintRequest request)
    {
        try
        {
            _logger.LogInformation("发送打印请求 - AET: {AET}, Host: {Host}, Port: {Port}", 
                request.CalledAE, request.HostName, request.Port);

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