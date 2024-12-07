using System;

namespace DicomSCP.Models;

public class PrintJob
{
    // 基本信息
    public string JobId { get; set; } = "";
    public string FilmSessionId { get; set; } = "";
    public string FilmBoxId { get; set; } = "";
    public string CallingAE { get; set; } = "";
    public PrintJobStatus Status { get; set; }
    public string ErrorMessage { get; set; } = "";

    // Film Session 参数
    public int NumberOfCopies { get; set; } = 1;
    public string PrintPriority { get; set; } = "LOW";
    public string MediumType { get; set; } = "BLUE FILM";
    public string FilmDestination { get; set; } = "MAGAZINE";

    // Film Box 参数
    public bool PrintInColor { get; set; } = false;
    public string FilmOrientation { get; set; } = "PORTRAIT";
    public string FilmSizeID { get; set; } = "8INX10IN";
    public string ImageDisplayFormat { get; set; } = "STANDARD\\1,1";
    public string MagnificationType { get; set; } = "REPLICATE";
    public string SmoothingType { get; set; } = "MEDIUM";
    public string BorderDensity { get; set; } = "BLACK";
    public string EmptyImageDensity { get; set; } = "BLACK";
    public string Trim { get; set; } = "NO";

    // 图像信息
    public string ImagePath { get; set; } = "";

    // 研究信息
    public string StudyInstanceUID { get; set; } = "";

    // 时间戳
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

public enum PrintJobStatus
{
    Created,        // 作业创建(Film Session创建时)
    ImageReceived,  // 图像接收完成(保存图像后)
    Failed          // 接收失败
}

// 以下是新添加的模型

// 打印请求模型
public class PrintRequest
{
    public string FilePath { get; set; } = string.Empty;
    public string CalledAE { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; }
    public int NumberOfCopies { get; set; } = 1;
    public string PrintPriority { get; set; } = string.Empty;
    public string MediumType { get; set; } = string.Empty;
    public string FilmDestination { get; set; } = string.Empty;
    public string FilmOrientation { get; set; } = string.Empty;
    public string FilmSizeID { get; set; } = string.Empty;
    public string ImageDisplayFormat { get; set; } = string.Empty;
    public string MagnificationType { get; set; } = string.Empty;
    public string SmoothingType { get; set; } = string.Empty;
    public string BorderDensity { get; set; } = string.Empty;
    public string EmptyImageDensity { get; set; } = string.Empty;
    public string Trim { get; set; } = string.Empty;
}

// 打印机配置模型
public class PrinterConfig
{
    public string Name { get; set; } = string.Empty;
    public string AeTitle { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool IsDefault { get; set; }
    public string Description { get; set; } = string.Empty;
}

// PrintSCU 配置模型
public class PrintScuConfig
{
    public string AeTitle { get; set; } = string.Empty;
    public List<PrinterConfig> Printers { get; set; } = new();
} 