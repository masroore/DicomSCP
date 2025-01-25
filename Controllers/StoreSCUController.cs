using Microsoft.AspNetCore.Mvc;
using DicomSCP.Services;
using DicomSCP.Configuration;
using Microsoft.Extensions.Options;
using DicomSCP.Data;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StoreSCUController : ControllerBase
{
    private readonly IStoreSCU _storeSCU;
    private readonly QueryRetrieveConfig _config;
    private readonly string _tempPath;

    public StoreSCUController(
        IStoreSCU storeSCU,
        IOptions<QueryRetrieveConfig> config,
        IOptions<DicomSettings> settings)
    {
        _storeSCU = storeSCU;
        _config = config.Value;
        _tempPath = settings.Value.TempPath;
    }

    [HttpGet("nodes")]
    public ActionResult<IEnumerable<RemoteNode>> GetNodes()
    {
        // Only return nodes that support storage
        var storeNodes = _config.RemoteNodes.Where(n => n.SupportsStore());
        return Ok(storeNodes);
    }

    [HttpPost("verify/{remoteName}")]
    public async Task<IActionResult> VerifyConnection(string remoteName)
    {
        try
        {
            var result = await _storeSCU.VerifyConnectionAsync(remoteName);
            return Ok(new { success = result });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "Failed to verify connection");
            return StatusCode(500, "Failed to verify connection");
        }
    }

    [HttpPost("send/{remoteName}")]
    public async Task<IActionResult> SendFiles(string remoteName, [FromForm] string? folderPath, IFormFileCollection? files)
    {
        try
        {
            var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == remoteName);
            if (node == null)
            {
                return NotFound($"Node not found: {remoteName}");
            }

            // Verify if the node supports storage
            if (!node.SupportsStore())
            {
                return BadRequest($"Node {remoteName} does not support storage operations");
            }

            if (!string.IsNullOrEmpty(folderPath))
            {
                DicomLogger.Information("Api", "Starting to send folder - Path: {FolderPath}, Target: {RemoteName}",
                    folderPath, remoteName);

                await _storeSCU.SendFolderAsync(folderPath, remoteName);
                return Ok(new { message = "Folder sent successfully" });
            }
            else if (files != null && files.Count > 0)
            {
                DicomLogger.Information("Api", "Starting to upload files - File count: {Count}, Target: {RemoteName}",
                    files.Count, remoteName);

                // Create temporary directory
                var tempDir = Path.Combine(_tempPath, "temp_" + DateTime.Now.Ticks.ToString());
                Directory.CreateDirectory(tempDir);

                try
                {
                    // Save uploaded files
                    var filePaths = new List<string>();
                    foreach (var file in files)
                    {
                        // Save directly to temporary directory using the file name
                        var fileName = Path.GetFileName(file.FileName);
                        var filePath = Path.Combine(tempDir, fileName);

                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }
                        filePaths.Add(filePath);
                        DicomLogger.Debug("Api", "File saved to temporary directory - File: {FileName}", file.FileName);
                    }

                    // Send files
                    await _storeSCU.SendFilesAsync(filePaths, remoteName);
                    return Ok(new { message = "Files sent successfully" });
                }
                finally
                {
                    // Clean up temporary files
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                        DicomLogger.Debug("Api", "Temporary directory cleaned up - Path: {TempDir}", tempDir);
                    }
                }
            }
            else
            {
                DicomLogger.Warning("Api", "No files or folder path provided");
                return BadRequest("Please provide files or folder path");
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "Failed to send files");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("sendStudy/{remoteName}/{studyInstanceUid}")]
    public async Task<IActionResult> SendStudy(string remoteName, string studyInstanceUid)
    {
        try
        {
            var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == remoteName);
            if (node == null)
            {
                return NotFound($"Node not found: {remoteName}");
            }

            // Verify if the node supports storage
            if (!node.SupportsStore())
            {
                return BadRequest($"Node {remoteName} does not support storage operations");
            }

            // Get all file paths related to the study
            var repository = HttpContext.RequestServices.GetRequiredService<DicomRepository>();
            var instances = repository.GetInstancesByStudyUid(studyInstanceUid);

            if (!instances.Any())
            {
                return NotFound("No related images found");
            }

            // Get full paths of all files
            var settings = HttpContext.RequestServices.GetRequiredService<IOptions<DicomSettings>>().Value;
            var filePaths = instances.Select(i => Path.Combine(settings.StoragePath, i.FilePath));

            // Send files
            await _storeSCU.SendFilesAsync(filePaths, remoteName);

            return Ok(new
            {
                message = "Sent successfully",
                totalFiles = filePaths.Count()
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "Failed to send study - StudyInstanceUid: {StudyUid}", studyInstanceUid);
            return StatusCode(500, new { message = "Failed to send" });
        }
    }
