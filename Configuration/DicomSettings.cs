using System.ComponentModel.DataAnnotations;
using Serilog.Events;

namespace DicomSCP.Configuration;

public class DicomSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "STORESCP";

    [Range(1, 65535)]
    public int StoreSCPPort { get; set; } = 11112;

    [Range(1, 65535)]
    public int WorklistSCPPort { get; set; } = 11113;

    [Required]
    public string StoragePath { get; set; } = "./received_files";

    [Required]
    public string TempPath { get; set; } = "./temp_files";

    public string ConnectionString { get; set; } = "Data Source=dicom.db";

    public AdvancedSettings Advanced { get; set; } = new();

    public SwaggerSettings Swagger { get; set; } = new();
}

public class AdvancedSettings
{
    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
    public int ConcurrentStoreLimit { get; set; } = 0;
    
    public bool EnableCompression { get; set; } = false;
    public string PreferredTransferSyntax { get; set; } = "JPEG2000Lossless";
}

public class SwaggerSettings
{
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = "DICOM SCP API";
    public string Version { get; set; } = "v1";
    public string Description { get; set; } = "DICOM SCP服务器的REST API";
} 