using Microsoft.AspNetCore.Mvc;
using DicomSCP.Configuration;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public LogsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("types")]
    public IActionResult GetLogTypes()
    {
        try
        {
            var logSettings = _configuration.GetSection("Logging").Get<LogSettings>();
            if (logSettings?.Services == null)
            {
                return Ok(Array.Empty<string>());
            }

            // Get all log types for services
            var types = new List<string>
            {
                "QRSCP",
                "QueryRetrieveSCU",
                "StoreSCP",
                "StoreSCU",
                "WorklistSCP",
                "PrintSCP",
                "PrintSCU",
                "WADO",
                "Database",
                "Api"
            };

            return Ok(types);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to get log types: {ex.Message}");
        }
    }

    [HttpGet("files/{type}")]
    public IActionResult GetLogFiles(string type)
    {
        try
        {
            var logSettings = _configuration.GetSection("Logging").Get<LogSettings>();
            if (logSettings == null)
            {
                return NotFound("Log configuration not found");
            }

            // Get log path based on type
            string? logPath = type.ToLower() switch
            {
                "qrscp" => logSettings.Services.QRSCP.LogPath,
                "queryretrievescu" => logSettings.Services.QueryRetrieveSCU.LogPath,
                "storescp" => logSettings.Services.StoreSCP.LogPath,
                "storescu" => logSettings.Services.StoreSCU.LogPath,
                "worklistscp" => logSettings.Services.WorklistSCP.LogPath,
                "printscp" => logSettings.Services.PrintSCP.LogPath,
                "printscu" => logSettings.Services.PrintSCU.LogPath,
                "wado" => logSettings.Services.WADO.LogPath,
                "database" => logSettings.Database.LogPath,
                "api" => logSettings.Api.LogPath,
                _ => null
            };

            if (string.IsNullOrEmpty(logPath))
            {
                return NotFound("Log path configuration not found");
            }

            if (!Directory.Exists(logPath))
            {
                return Ok(Array.Empty<object>());
            }

            // Get list of log files
            var files = Directory.GetFiles(logPath, "*.log")
                .Select(f => new FileInfo(f))
                .Select(f => new
                {
                    name = f.Name,
                    size = f.Length,
                    lastModified = f.LastWriteTime
                })
                .OrderByDescending(f => f.lastModified)
                .ToList();

            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to get log file list: {ex.Message}");
        }
    }

    [HttpDelete("{type}/{filename}")]
    public IActionResult DeleteLogFile(string type, string filename)
    {
        try
        {
            var logSettings = _configuration.GetSection("Logging").Get<LogSettings>();
            if (logSettings == null)
            {
                return NotFound("Log configuration not found");
            }

            // Get log path based on type
            string? logPath = type.ToLower() switch
            {
                "qrscp" => logSettings.Services.QRSCP.LogPath,
                "queryretrievescu" => logSettings.Services.QueryRetrieveSCU.LogPath,
                "storescp" => logSettings.Services.StoreSCP.LogPath,
                "storescu" => logSettings.Services.StoreSCU.LogPath,
                "worklistscp" => logSettings.Services.WorklistSCP.LogPath,
                "printscp" => logSettings.Services.PrintSCP.LogPath,
                "printscu" => logSettings.Services.PrintSCU.LogPath,
                "wado" => logSettings.Services.WADO.LogPath,
                "database" => logSettings.Database.LogPath,
                "api" => logSettings.Api.LogPath,
                _ => null
            };

            if (string.IsNullOrEmpty(logPath))
            {
                return NotFound("Log path configuration not found");
            }

            var filePath = Path.Combine(logPath, filename);

            // Validate if the filename is valid (only .log files are allowed)
            if (!filename.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                filename.Contains("..") ||
                !System.IO.File.Exists(filePath))
            {
                return NotFound("File does not exist or is not a valid log file");
            }

            // Do not allow deletion of today's log file
            if (filename.Contains(DateTime.Now.ToString("yyyyMMdd")))
            {
                return BadRequest("Today's log file cannot be deleted");
            }

            try
            {
                // Try renaming the file
                var tempPath = Path.Combine(logPath, $"{Guid.NewGuid()}.tmp");
                System.IO.File.Move(filePath, tempPath);

                // If renaming is successful, delete the temporary file
                System.IO.File.Delete(tempPath);
                return Ok();
            }
            catch (IOException ex) when ((ex.HResult & 0x0000FFFF) == 32) // File is in use
            {
                return StatusCode(409, "File is in use and cannot be deleted");
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to delete log file: {ex.Message}");
        }
    }

    [HttpGet("{type}/{filename}/content")]
    public IActionResult GetLogContent(string type, string filename, [FromQuery] int? maxLines = 1000)
    {
        try
        {
            var logSettings = _configuration.GetSection("Logging").Get<LogSettings>();
            if (logSettings == null)
            {
                return NotFound("Log configuration not found");
            }

            // Get log path based on type
            string? logPath = type.ToLower() switch
            {
                "qrscp" => logSettings.Services.QRSCP.LogPath,
                "queryretrievescu" => logSettings.Services.QueryRetrieveSCU.LogPath,
                "storescp" => logSettings.Services.StoreSCP.LogPath,
                "storescu" => logSettings.Services.StoreSCU.LogPath,
                "worklistscp" => logSettings.Services.WorklistSCP.LogPath,
                "printscp" => logSettings.Services.PrintSCP.LogPath,
                "printscu" => logSettings.Services.PrintSCU.LogPath,
                "wado" => logSettings.Services.WADO.LogPath,
                "database" => logSettings.Database.LogPath,
                "api" => logSettings.Api.LogPath,
                _ => null
            };

            if (string.IsNullOrEmpty(logPath))
            {
                return NotFound("Log path configuration not found");
            }

            var filePath = Path.Combine(logPath, filename);

            // Validate if the filename is valid
            if (!filename.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                filename.Contains("..") ||
                !System.IO.File.Exists(filePath))
            {
                return NotFound("File does not exist or is not a valid log file");
            }

            // Read log content
            var lines = new List<string>();
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                    // If exceeding max lines, keep only the last lines
                    if (maxLines.HasValue && lines.Count > maxLines.Value)
                    {
                        lines.RemoveAt(0);
                    }
                }
            }

            return Ok(new { content = lines });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to read log content: {ex.Message}");
        }
    }
}
