namespace DicomSCP.Models;

public class PrintJob
{
    public string JobId { get; set; } = string.Empty;
    public string FilmSessionId { get; set; } = string.Empty;
    public string FilmBoxId { get; set; } = string.Empty;
    public string ImageBoxId { get; set; } = string.Empty;
    public string CallingAE { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING";  // PENDING, PRINTING, COMPLETED, FAILED
    public string? ImagePath { get; set; }
    public string? PrinterName { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
} 