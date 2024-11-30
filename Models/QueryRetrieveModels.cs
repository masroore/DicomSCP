using System.Text.Json.Serialization;

namespace DicomSCP.Models;

public class DicomNodeConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("aeTitle")]
    public string AeTitle { get; set; } = string.Empty;
    
    [JsonPropertyName("hostName")]
    public string HostName { get; set; } = string.Empty;
    
    [JsonPropertyName("port")]
    public int Port { get; set; }
    
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}

public class QueryRetrieveConfig
{
    public string LocalAeTitle { get; set; } = "QUERYSCU";
    public int LocalPort { get; set; } = 11114;
    public List<DicomNodeConfig> RemoteNodes { get; set; } = new();
}

public class QueryRequest
{
    [JsonPropertyName("patientId")]
    public string? PatientId { get; set; }

    [JsonPropertyName("patientName")]
    public string? PatientName { get; set; }

    [JsonPropertyName("accessionNumber")]
    public string? AccessionNumber { get; set; }

    [JsonPropertyName("studyDate")]
    public string? StudyDate { get; set; }

    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("studyInstanceUid")]
    public string? StudyInstanceUid { get; set; }

    [JsonPropertyName("seriesInstanceUid")]
    public string? SeriesInstanceUid { get; set; }
}

public class MoveRequest
{
    [JsonPropertyName("destinationAe")]
    public string DestinationAe { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = "STUDY";  // STUDY, SERIES, IMAGE

    [JsonPropertyName("studyInstanceUid")]
    public string StudyInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("seriesInstanceUid")]
    public string? SeriesInstanceUid { get; set; }

    [JsonPropertyName("sopInstanceUid")]
    public string? SopInstanceUid { get; set; }
}

public class DicomStudy
{
    [JsonPropertyName("studyInstanceUid")]
    public string StudyInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("studyDate")]
    public string StudyDate { get; set; } = string.Empty;

    [JsonPropertyName("patientId")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("patientName")]
    public string PatientName { get; set; } = string.Empty;

    [JsonPropertyName("accessionNumber")]
    public string AccessionNumber { get; set; } = string.Empty;

    [JsonPropertyName("studyDescription")]
    public string StudyDescription { get; set; } = string.Empty;

    [JsonPropertyName("modalities")]
    public string Modalities { get; set; } = string.Empty;

    [JsonPropertyName("seriesCount")]
    public int SeriesCount { get; set; }

    [JsonPropertyName("instanceCount")]
    public int InstanceCount { get; set; }
}

public class DicomSeries
{
    [JsonPropertyName("seriesInstanceUid")]
    public string SeriesInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("studyInstanceUid")]
    public string StudyInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("seriesNumber")]
    public string SeriesNumber { get; set; } = string.Empty;

    [JsonPropertyName("seriesDescription")]
    public string SeriesDescription { get; set; } = string.Empty;

    [JsonPropertyName("modality")]
    public string Modality { get; set; } = string.Empty;

    [JsonPropertyName("instanceCount")]
    public int InstanceCount { get; set; }
}

public class QueryResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public IEnumerable<T>? Data { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public class MoveResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;
} 