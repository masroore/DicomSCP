using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using DicomSCP.Configuration;

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
            var jsonDocument = JsonDocument.Parse(jsonString);
            var root = jsonDocument.RootElement;

            var dicomSettings = root.GetProperty("DicomSettings").Deserialize<DicomSettings>();
            var queryRetrieveConfig = root.GetProperty("QueryRetrieveConfig").Deserialize<QueryRetrieveConfig>();

            if (dicomSettings == null || queryRetrieveConfig == null)
            {
                return StatusCode(500, "配置文件格式错误");
            }

            // 只返回可编辑的配置项
            var editableSettings = new EditableSettings
            {
                AeTitle = dicomSettings.AeTitle ?? "",
                StoreSCPPort = dicomSettings.StoreSCPPort,
                StoragePath = dicomSettings.StoragePath ?? "",
                TempPath = dicomSettings.TempPath ?? "",
                
                // 高级配置
                Advanced = new()
                {
                    ValidateCallingAE = dicomSettings.Advanced?.ValidateCallingAE ?? false,
                    AllowedCallingAEs = dicomSettings.Advanced?.AllowedCallingAEs?.ToList() ?? new(),
                    ConcurrentStoreLimit = dicomSettings.Advanced?.ConcurrentStoreLimit ?? 0,
                    EnableCompression = dicomSettings.Advanced?.EnableCompression ?? false,
                    PreferredTransferSyntax = dicomSettings.Advanced?.PreferredTransferSyntax ?? ""
                },
                
                WorklistSCP = new()
                {
                    AeTitle = dicomSettings.WorklistSCP?.AeTitle ?? "",
                    Port = dicomSettings.WorklistSCP?.Port ?? 0,
                    ValidateCallingAE = dicomSettings.WorklistSCP?.ValidateCallingAE ?? false,
                    AllowedCallingAEs = dicomSettings.WorklistSCP?.AllowedCallingAEs?.ToList() ?? new()
                },
                
                QRSCP = new()
                {
                    AeTitle = dicomSettings.QRSCP?.AeTitle ?? "",
                    Port = dicomSettings.QRSCP?.Port ?? 0,
                    ValidateCallingAE = dicomSettings.QRSCP?.ValidateCallingAE ?? false,
                    AllowedCallingAETitles = dicomSettings.QRSCP?.AllowedCallingAETitles ?? new(),
                    EnableCGet = dicomSettings.QRSCP?.EnableCGet ?? false,
                    EnableCMove = dicomSettings.QRSCP?.EnableCMove ?? false,
                    MoveDestinations = dicomSettings.QRSCP?.MoveDestinations?.Select(m => new EditableSettings.MoveDestination
                    {
                        Name = m.Name ?? "",
                        AeTitle = m.AeTitle ?? "",
                        HostName = m.HostName ?? "",
                        Port = m.Port,
                        IsDefault = m.IsDefault
                    }).ToList() ?? new()
                },
                
                QueryRetrieve = new()
                {
                    LocalAeTitle = queryRetrieveConfig.LocalAeTitle ?? "",
                    LocalPort = queryRetrieveConfig.LocalPort,
                    RemoteNodes = queryRetrieveConfig.RemoteNodes?.Select(n => new RemoteNode
                    {
                        Name = n.Name ?? "",
                        AeTitle = n.AeTitle ?? "",
                        HostName = n.HostName ?? "",
                        Port = n.Port,
                        IsDefault = n.IsDefault
                    }).ToList() ?? new()
                }
            };

            return Ok(JsonSerializer.Serialize(editableSettings, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取配置文件失败");
            return StatusCode(500, "读取配置文件失败");
        }
    }

    [HttpPost]
    public IActionResult UpdateConfig([FromBody] EditableSettings config)
    {
        try
        {
            var jsonString = System.IO.File.ReadAllText(_configPath);
            var jsonDocument = JsonDocument.Parse(jsonString);
            var root = jsonDocument.RootElement;

            var dicomSettings = root.GetProperty("DicomSettings").Deserialize<DicomSettings>();
            var queryRetrieveConfig = root.GetProperty("QueryRetrieveConfig").Deserialize<QueryRetrieveConfig>();

            if (dicomSettings == null || queryRetrieveConfig == null)
            {
                return StatusCode(500, "配置文件格式错误");
            }

            // 更新 DicomSettings
            dicomSettings.AeTitle = config.AeTitle ?? "";
            dicomSettings.StoreSCPPort = config.StoreSCPPort;
            dicomSettings.StoragePath = config.StoragePath ?? "";
            dicomSettings.TempPath = config.TempPath ?? "";

            // 确保 Advanced 不为 null
            if (dicomSettings.Advanced == null)
            {
                dicomSettings.Advanced = new();
            }

            // 更新高级配置
            dicomSettings.Advanced.ValidateCallingAE = config.Advanced.ValidateCallingAE;
            dicomSettings.Advanced.AllowedCallingAEs = config.Advanced.AllowedCallingAEs?.ToArray() ?? Array.Empty<string>();
            dicomSettings.Advanced.ConcurrentStoreLimit = config.Advanced.ConcurrentStoreLimit;
            dicomSettings.Advanced.EnableCompression = config.Advanced.EnableCompression;
            dicomSettings.Advanced.PreferredTransferSyntax = config.Advanced.PreferredTransferSyntax ?? "";

            // 确保 WorklistSCP 不为 null
            if (dicomSettings.WorklistSCP == null)
            {
                dicomSettings.WorklistSCP = new();
            }

            // 更新 WorklistSCP 配置
            dicomSettings.WorklistSCP.AeTitle = config.WorklistSCP.AeTitle ?? "";
            dicomSettings.WorklistSCP.Port = config.WorklistSCP.Port;
            dicomSettings.WorklistSCP.ValidateCallingAE = config.WorklistSCP.ValidateCallingAE;
            dicomSettings.WorklistSCP.AllowedCallingAEs = config.WorklistSCP.AllowedCallingAEs?.ToArray() ?? Array.Empty<string>();

            // 确保 QRSCP 不为 null
            if (dicomSettings.QRSCP == null)
            {
                dicomSettings.QRSCP = new();
            }

            // 更新 QRSCP 配置
            dicomSettings.QRSCP.AeTitle = config.QRSCP.AeTitle ?? "";
            dicomSettings.QRSCP.Port = config.QRSCP.Port;
            dicomSettings.QRSCP.ValidateCallingAE = config.QRSCP.ValidateCallingAE;
            dicomSettings.QRSCP.AllowedCallingAETitles = config.QRSCP.AllowedCallingAETitles;
            dicomSettings.QRSCP.EnableCGet = config.QRSCP.EnableCGet;
            dicomSettings.QRSCP.EnableCMove = config.QRSCP.EnableCMove;
            dicomSettings.QRSCP.MoveDestinations = config.QRSCP.MoveDestinations?.Select(m => new MoveDestination
            {
                Name = m.Name ?? "",
                AeTitle = m.AeTitle ?? "",
                HostName = m.HostName ?? "",
                Port = m.Port,
                IsDefault = m.IsDefault
            }).ToList() ?? new();

            // 更新查询检索配置
            queryRetrieveConfig.LocalAeTitle = config.QueryRetrieve.LocalAeTitle ?? "";
            queryRetrieveConfig.LocalPort = config.QueryRetrieve.LocalPort;
            queryRetrieveConfig.RemoteNodes = config.QueryRetrieve.RemoteNodes?.Select(n => new RemoteNode
            {
                Name = n.Name ?? "",
                AeTitle = n.AeTitle ?? "",
                HostName = n.HostName ?? "",
                Port = n.Port,
                IsDefault = n.IsDefault
            }).ToList() ?? new();

            // 验证配置
            if (string.IsNullOrEmpty(dicomSettings.AeTitle))
            {
                return BadRequest("配置错误：AE Title 不能为空");
            }
            if (dicomSettings.StoreSCPPort <= 0)
            {
                return BadRequest("配置错误：StoreSCP 端口必须大于0");
            }
            // ... 其他验证

            // 保存配置
            var updatedJson = JsonSerializer.SerializeToDocument(new
            {
                DicomSettings = dicomSettings,
                QueryRetrieveConfig = queryRetrieveConfig,
                // 保持其他配置不变
                Logging = root.GetProperty("Logging"),
                AllowedHosts = root.GetProperty("AllowedHosts"),
                Kestrel = root.GetProperty("Kestrel"),
                ConnectionStrings = root.GetProperty("ConnectionStrings")
            }, new JsonSerializerOptions { WriteIndented = true });

            System.IO.File.WriteAllText(_configPath, updatedJson.RootElement.ToString());

            return Ok(new { message = "配置已更新，部分配置可能需要重启服务或程序才能生效" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新配置文件失败");
            return StatusCode(500, "更新配置文件失败");
        }
    }
} 