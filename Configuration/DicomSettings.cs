using System.ComponentModel.DataAnnotations;
using Serilog.Events;

namespace DicomSCP.Configuration;

public class DicomSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "STORESCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11112;

    [Required]
    public string StoragePath { get; set; } = "./received_files";

    public LogSettings Logging { get; set; } = new();

    public AdvancedSettings Advanced { get; set; } = new();

    public SwaggerSettings Swagger { get; set; } = new();
}

public class LogSettings
{
    public bool EnableConsoleLog { get; set; } = true;
    public bool EnableFileLog { get; set; } = true;
    public LogEventLevel FileLogLevel { get; set; } = LogEventLevel.Debug;
    public int RetainedDays { get; set; } = 31;
    public string LogPath { get; set; } = "logs";
}

public class AdvancedSettings
{
    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
    public int ConcurrentStoreLimit { get; set; } = 8;
    public int TempFileCleanupDelay { get; set; } = 300;
}

public class SwaggerSettings
{
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = "DICOM SCP API";
    public string Version { get; set; } = "v1";
    public string Description { get; set; } = "DICOM SCP服务器的REST API";
} 