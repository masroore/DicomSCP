namespace DicomSCP.Configuration;

public class SwaggerSettings
{
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = "DICOM SCP API";
    public string Version { get; set; } = "v1";
    public string Description { get; set; } = "DICOM SCP服务器的REST API";
} 