using System.ComponentModel.DataAnnotations;

namespace DicomSCP.Configuration;

public class DicomSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$", ErrorMessage = "AE Title must be 1-16 characters and contain only letters, numbers, hyphen and underscore")]
    public string AeTitle { get; set; } = "STORESCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11112;

    [Required]
    public string StoragePath { get; set; } = "./received_files";

    public int MaxPDULength { get; set; } = 16384;

    public bool ValidateCallingAE { get; set; } = false;

    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
} 