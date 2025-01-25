using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly string _configPath;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(IWebHostEnvironment env, ILogger<ConfigController> logger)
    {
        _configPath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetConfig()
    {
        try
        {
            var jsonString = System.IO.File.ReadAllText(_configPath);
            // Validate if it is a valid JSON
            using var document = JsonDocument.Parse(jsonString);

            // Return formatted JSON, preserving Chinese characters
            return Ok(JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
        catch (JsonException)
        {
            return StatusCode(500, "Configuration file format error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read configuration file");
            return StatusCode(500, "Failed to read configuration file");
        }
    }

    [HttpPost]
    public IActionResult UpdateConfig([FromBody] JsonElement config)
    {
        try
        {
            // Validate necessary configuration items
            try
            {
                var dicomSettings = config.GetProperty("DicomSettings");
                var aeTitle = dicomSettings.GetProperty("AeTitle").GetString();
                var storeSCPPort = dicomSettings.GetProperty("StoreSCPPort").GetInt32();
                var storagePath = dicomSettings.GetProperty("StoragePath").GetString();

                if (string.IsNullOrEmpty(aeTitle))
                {
                    return BadRequest("Configuration error: AE Title cannot be empty");
                }
                if (storeSCPPort <= 0 || storeSCPPort > 65535)
                {
                    return BadRequest("Configuration error: StoreSCP port must be between 1-65535");
                }
                if (string.IsNullOrEmpty(storagePath))
                {
                    return BadRequest("Configuration error: Storage path cannot be empty");
                }
            }
            catch (KeyNotFoundException)
            {
                return BadRequest("Configuration error: Missing necessary configuration items");
            }
            catch (InvalidOperationException)
            {
                return BadRequest("Configuration error: Configuration item format is incorrect");
            }

            // Save configuration
            var updatedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            System.IO.File.WriteAllText(_configPath, updatedJson);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update configuration file");
            return StatusCode(500, "Failed to update configuration file");
        }
    }
}
