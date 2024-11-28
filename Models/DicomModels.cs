namespace DicomSCP.Models;

public class Patient
{
    public string PatientId { get; set; } = null!;
    public string? PatientName { get; set; }
    public string? PatientBirthDate { get; set; }
    public string? PatientSex { get; set; }
    public DateTime CreateTime { get; set; }
}

public class Study
{
    public string StudyInstanceUid { get; set; } = null!;
    public string PatientId { get; set; } = null!;
    public string? StudyDate { get; set; }
    public string? StudyTime { get; set; }
    public string? StudyDescription { get; set; }
    public string? AccessionNumber { get; set; }
    public DateTime CreateTime { get; set; }
}

public class Series
{
    public string SeriesInstanceUid { get; set; } = null!;
    public string StudyInstanceUid { get; set; } = null!;
    public string? Modality { get; set; }
    public string? SeriesNumber { get; set; }
    public string? SeriesDescription { get; set; }
    public DateTime CreateTime { get; set; }
}

public class Instance
{
    public string SopInstanceUid { get; set; } = null!;
    public string SeriesInstanceUid { get; set; } = null!;
    public string SopClassUid { get; set; } = null!;
    public string? InstanceNumber { get; set; }
    public string FilePath { get; set; } = null!;
    public DateTime CreateTime { get; set; }
}