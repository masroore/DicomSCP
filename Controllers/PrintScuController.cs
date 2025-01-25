using Microsoft.AspNetCore.Mvc;
using DicomSCP.Services;
using DicomSCP.Models;
using DicomSCP.Data;
using DicomSCP.Configuration;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrintScuController : ControllerBase
{
    private readonly IPrintSCU _printSCU;
    private readonly DicomRepository _repository;
    private readonly ILogger<PrintScuController> _logger;
    private readonly DicomSettings _settings;

    public PrintScuController(
        IPrintSCU printSCU,
        DicomRepository repository,
        ILogger<PrintScuController> logger,
        IOptions<DicomSettings> settings)
    {
        _printSCU = printSCU;
        _repository = repository;
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Get the list of printers
    /// </summary>
    [HttpGet("printers")]
    public ActionResult<IEnumerable<Configuration.PrinterConfig>> GetPrinters()
    {
        var printers = _repository.GetPrinters();
        return Ok(printers);
    }

    /// <summary>
    /// Send a print request
    /// </summary>
    /// <remarks>
    /// Example request:
    /// ```json
    /// {
    ///     "filePath": "D:/dicom/image.dcm",
    ///     "calledAE": "PRINT-SCP",
    ///     "hostName": "192.168.1.100",
    ///     "port": 104,
    ///     "numberOfCopies": 1,
    ///     "enableDpi": false,
    ///     "dpi": 150,
    ///     "printPriority": "MEDIUM",
    ///     "mediumType": "BLUE FILM",
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
    /// Valid values:
    /// - enableDpi: true/false, default false
    /// - dpi: 100-300, default 150, only generated when enableDpi=true
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
            // Validate number of copies
            if (request.NumberOfCopies < 1)
            {
                return BadRequest(new { message = "Number of copies must be greater than 0" });
            }

            // Validate DPI value only if DPI is enabled
            if (request.EnableDpi)
            {
                if (!request.Dpi.HasValue)
                {
                    return BadRequest(new { message = "DPI value must be specified when DPI is enabled" });
                }
                if (request.Dpi.Value < 100 || request.Dpi.Value > 300)
                {
                    return BadRequest(new { message = "DPI value must be between 100 and 300" });
                }
            }
            else
            {
                // Clear DPI value if DPI is not enabled
                request.Dpi = null;
            }

            _logger.LogInformation("Sending print request - AET: {AET}, Host: {Host}, Port: {Port}, DPI: {DPI}, Copies: {Copies}",
                request.CalledAE,
                request.HostName,
                request.Port,
                request.EnableDpi ? request.Dpi.ToString() : "Not enabled",
                request.NumberOfCopies);

            // Send print request
            var success = await _printSCU.PrintAsync(request);

            if (success)
                return Ok(new { message = "Print request sent" });

            return StatusCode(500, new { message = "Print failed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing print request");
            return StatusCode(500, new { message = "Print failed", error = ex.Message });
        }
    }

    /// <summary>
    /// Test printer connection
    /// </summary>
    [HttpPost("printers/{name}/test")]
    public async Task<ActionResult> TestPrinter(string name)
    {
        try
        {
            var printers = _repository.GetPrinters();

            var printer = printers.FirstOrDefault(p => p.Name == name);
            if (printer == null)
                return NotFound($"Printer '{name}' not found");

            var success = await _printSCU.VerifyAsync(
                printer.HostName,
                printer.Port,
                printer.AeTitle);

            if (success)
                return Ok(new {
                    success = true,
                    message = "Printer connection test successful"
                });

            return Ok(new {
                success = false,
                message = "Printer connection refused, please check printer configuration and status"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing printer connection");
            return Ok(new {
                success = false,
                message = "Failed to connect to printer, please check network connection"
            });
        }
    }

    [HttpPost("print-by-job")]
    public async Task<IActionResult> PrintByJobId([FromBody] PrintByJobRequest request)
    {
        try
        {
            // Get print job
            var printJob = await _repository.GetPrintJobByIdAsync(request.JobId);
            if (printJob == null)
            {
                return NotFound($"Print job not found: {request.JobId}");
            }

            // Get printer configuration
            var printer = _repository.GetPrinterByName(request.PrinterName);
            if (printer == null)
            {
                return NotFound($"Printer not found: {request.PrinterName}");
            }

            // Build full file path
            var fullPath = Path.Combine(_settings.StoragePath, printJob.ImagePath);
            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogError("Print file not found: {FilePath}", fullPath);
                return NotFound($"Print file not found: {fullPath}");
            }

            // Build print request
            var printRequest = new PrintRequest
            {
                FilePath = fullPath,  // Use full path
                HostName = printer.HostName,
                Port = printer.Port,
                CalledAE = printer.AeTitle,

                // Use default print parameters or get from database
                PrintPriority = printJob.PrintPriority,
                MediumType = printJob.MediumType,
                FilmDestination = printJob.FilmDestination,
                NumberOfCopies = printJob.NumberOfCopies,
                FilmOrientation = printJob.FilmOrientation,
                FilmSizeID = printJob.FilmSizeID,
                ImageDisplayFormat = "STANDARD\\1,1",  // Fixed to 1,1 layout
                MagnificationType = printJob.MagnificationType,
                SmoothingType = printJob.SmoothingType,
                BorderDensity = printJob.BorderDensity,
                EmptyImageDensity = printJob.EmptyImageDensity,
                Trim = printJob.Trim
            };

            // Execute print
            var result = await _printSCU.PrintAsync(printRequest);
            if (result)
            {
                await _repository.UpdatePrintJobStatusAsync(request.JobId, PrintJobStatus.Completed);
                return Ok(new { Message = "Print successful", JobId = request.JobId });
            }
            else
            {
                return BadRequest(new { Message = "Print failed", JobId = request.JobId });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Print failed");
            return StatusCode(500, new { Message = ex.Message });
        }
    }
}

// Request model
public class PrintByJobRequest
{
    [Required]
    public string JobId { get; set; } = "";

    [Required]
    public string PrinterName { get; set; } = "";
}
