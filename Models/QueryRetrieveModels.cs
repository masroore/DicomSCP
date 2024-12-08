using System.Text.Json.Serialization;
using DicomSCP.Configuration;
using FellowOakDicom;
using System.ComponentModel.DataAnnotations;
using FellowOakDicom.Network;

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

public class QueryRequest
{
    // Patient Level 支持的查询条件
    [JsonPropertyName("patientId")]
    public string? PatientId { get; set; }  // 病人ID

    [JsonPropertyName("patientName")]
    public string? PatientName { get; set; }  // 病人姓名

    [JsonPropertyName("patientBirthDate")]
    public string? PatientBirthDate { get; set; }  // 出生日期

    [JsonPropertyName("patientSex")]
    public string? PatientSex { get; set; }  // 性别

    // Study Level 支持的查询条件
    [JsonPropertyName("studyInstanceUid")]
    public string? StudyInstanceUid { get; set; }  // 检查实例UID

    [JsonPropertyName("studyDate")]
    public string? StudyDate { get; set; }  // 检查日期

    [JsonPropertyName("studyTime")]
    public string? StudyTime { get; set; }  // 检查时间

    [JsonPropertyName("accessionNumber")]
    public string? AccessionNumber { get; set; }  // 检查号

    [JsonPropertyName("studyDescription")]
    public string? StudyDescription { get; set; }  // 检查描述

    [JsonPropertyName("modality")]
    public string? Modality { get; set; }  // 检查设备类型

    // Series Level 支持的查询条件
    [JsonPropertyName("seriesInstanceUid")]
    public string? SeriesInstanceUid { get; set; }  // 序列实例UID

    [JsonPropertyName("seriesNumber")]
    public string? SeriesNumber { get; set; }  // 序列号

    [JsonPropertyName("seriesDescription")]
    public string? SeriesDescription { get; set; }  // 序列描述

    [JsonPropertyName("seriesModality")]
    public string? SeriesModality { get; set; }  // 序列设备类型

    // Image Level 支持的查询条件
    [JsonPropertyName("sopInstanceUid")]
    public string? SopInstanceUid { get; set; }  // 影像实例UID

    [JsonPropertyName("instanceNumber")]
    public string? InstanceNumber { get; set; }  // 影像号

    // 验证方法
    public bool ValidateImageLevelQuery()
    {
        return !string.IsNullOrEmpty(StudyInstanceUid) 
            && !string.IsNullOrEmpty(SeriesInstanceUid);
    }
}

public class MoveRequest
{
    /// <summary>
    /// 病人ID（当level为PATIENT时必填）
    /// </summary>
    [JsonPropertyName("patientId")]
    public string? PatientId { get; set; }

    /// <summary>
    /// 研究实例UID（当level为STUDY/SERIES/IMAGE时必填）
    /// </summary>
    [JsonPropertyName("studyInstanceUid")]
    public string? StudyInstanceUid { get; set; }

    /// <summary>
    /// 序列实例UID（当level为SERIES或IMAGE时必填）
    /// </summary>
    [JsonPropertyName("seriesInstanceUid")]
    public string? SeriesInstanceUid { get; set; }

    /// <summary>
    /// 影像实例UID（当level为IMAGE时必填）
    /// </summary>
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

public class DicomStudyResult
{
    [JsonPropertyName("studyInstanceUid")]
    public string StudyInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("studyDate")]
    public string StudyDate { get; set; } = string.Empty;

    [JsonPropertyName("studyTime")]
    public string StudyTime { get; set; } = string.Empty;

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

    public static DicomStudyResult FromDataset(DicomDataset dataset)
    {
        return new DicomStudyResult
        {
            StudyInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
            StudyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty),
            StudyTime = dataset.GetSingleValueOrDefault(DicomTag.StudyTime, string.Empty),
            PatientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            PatientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
            AccessionNumber = dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
            StudyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
            Modalities = dataset.GetSingleValueOrDefault(DicomTag.ModalitiesInStudy, string.Empty),
            SeriesCount = dataset.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedSeries, 0),
            InstanceCount = dataset.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedInstances, 0)
        };
    }
} 