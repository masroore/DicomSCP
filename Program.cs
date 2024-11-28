using FellowOakDicom;
using Serilog;
using System.Threading;

var builder = WebApplication.CreateBuilder(args);

// 配置Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/dicom-scp-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// 添加服务
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 注册DICOM服务
builder.Services.Configure<DicomSettings>(
    builder.Configuration.GetSection("DicomSettings"));
builder.Services.AddSingleton<DicomServer>();
builder.Services.AddHostedService<DicomBackgroundService>();

// 配置 CStoreSCP
var dicomSettings = builder.Configuration
    .GetSection("DicomSettings")
    .Get<DicomSettings>() ?? new DicomSettings();
CStoreSCP.Configure(dicomSettings.StoragePath, dicomSettings.AeTitle);

// 配置线程池
ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount);
ThreadPool.SetMaxThreads(Environment.ProcessorCount * 4, Environment.ProcessorCount * 2);

var app = builder.Build();

// 配置HTTP请求管道
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

public class DicomSettings
{
    public string AeTitle { get; set; } = "STORESCP";
    public int Port { get; set; } = 11112;
    public string StoragePath { get; set; } = "./received_files";
} 