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
    
    // 患者信息
    public string PatientId { get; set; } = string.Empty;        // 患者ID
    public string PatientName { get; set; } = string.Empty;      // 患者姓名
    public string AccessionNumber { get; set; } = string.Empty;  // 检查号
    
    // 打印参数
    public string FilmSize { get; set; } = string.Empty;          // 胶片尺寸
    public string FilmOrientation { get; set; } = string.Empty;   // 胶片方向
    public string FilmLayout { get; set; } = string.Empty;        // 胶片布局
    public string MagnificationType { get; set; } = string.Empty; // 放大类型
    public string BorderDensity { get; set; } = string.Empty;     // 边框密度
    public string EmptyImageDensity { get; set; } = string.Empty; // 空图像密度
    public string MinDensity { get; set; } = string.Empty;        // 最小密度
    public string MaxDensity { get; set; } = string.Empty;        // 最大密度
    public string Trim { get; set; } = string.Empty;              // 修剪
    public string ConfigurationInfo { get; set; } = string.Empty; // 配置信息
    
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
} 