using System;

namespace DicomSCP.Models;

public class PrintJob
{
    public string JobId { get; set; } = "";
    public string FilmSessionId { get; set; } = "";
    public string FilmBoxId { get; set; } = "";
    public string CallingAE { get; set; } = "";
    public string Status { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string AccessionNumber { get; set; } = "";
    public string FilmSize { get; set; } = "";
    public string FilmOrientation { get; set; } = "";
    public string FilmLayout { get; set; } = "";
    public string MagnificationType { get; set; } = "";
    public string BorderDensity { get; set; } = "";
    public string EmptyImageDensity { get; set; } = "";
    public string MinDensity { get; set; } = "";
    public string MaxDensity { get; set; } = "";
    public string TrimValue { get; set; } = "";
    public string ConfigurationInfo { get; set; } = "";
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
} 