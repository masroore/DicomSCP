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

    [Required]
    public string StoragePath { get; set; } = "./received_files";

    [Required]
    public string TempPath { get; set; } = "./temp_files";

    public AdvancedSettings Advanced { get; set; } = new();

    public WorklistSCPSettings WorklistSCP { get; set; } = new();

    public QRSCPSettings QRSCP { get; set; } = new();
}

public class WorklistSCPSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "WORKLISTSCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11113;

    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
}

public class AdvancedSettings
{
    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
    public int ConcurrentStoreLimit { get; set; } = 0;
    
    public bool EnableCompression { get; set; } = false;
    public string PreferredTransferSyntax { get; set; } = "JPEG2000Lossless";
}

public class QRSCPSettings
{
    public string AeTitle { get; set; } = "QR_SCP";
    public int Port { get; set; } = 11114;
    public bool EnableCGet { get; set; } = true;
    public bool EnableCMove { get; set; } = true;
    public bool ValidateCallingAE { get; set; } = false;
    public List<string> AllowedCallingAETitles { get; set; } = new List<string>();
    public List<MoveDestination> MoveDestinations { get; set; } = new List<MoveDestination>();
}

public class MoveDestination
{
    public string Name { get; set; } = "";
    public string AeTitle { get; set; } = "";
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 11112;
    public bool IsDefault { get; set; }
} 