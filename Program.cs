using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Data;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// 获取配置
var settings = builder.Configuration.GetSection("DicomSettings").Get<DicomSettings>() 
    ?? new DicomSettings();

// 配置日志
var logSettings = builder.Configuration
    .GetSection("DicomSettings:Logging")
    .Get<LogSettings>() ?? new LogSettings();

var logConfig = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning);

// 控制台日志 - 只显示服务状态和错误
if (logSettings.EnableConsoleLog)
{
    logConfig.WriteTo.Logger(lc => lc
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:l}{NewLine}",
            restrictedToMinimumLevel: LogEventLevel.Information
        )
        .Filter.ByIncludingOnly(evt => 
            evt.Level == LogEventLevel.Error || 
            evt.Level == LogEventLevel.Fatal || 
            evt.Properties.ContainsKey("Area")
        )
    );
}

// 文件日志 - 统一记录到一个文件
if (logSettings.EnableFileLog)
{
    logConfig.WriteTo.File(
        path: Path.Combine(logSettings.LogPath, "dicom-scp-.txt"),
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: logSettings.FileLogLevel,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: logSettings.RetainedDays,
        shared: true
    );
}

Log.Logger = logConfig.CreateLogger();
builder.Host.UseSerilog();

// 添加日志服务
builder.Services.AddLogging(loggingBuilder =>
    loggingBuilder.AddSerilog(dispose: true));

// 添加服务
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// 配置 Swagger
if (settings.Swagger.Enabled)
{
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc(settings.Swagger.Version, new OpenApiInfo
        {
            Title = settings.Swagger.Title,
            Version = settings.Swagger.Version,
            Description = settings.Swagger.Description
        });
    });
}

// DICOM服务注册
builder.Services.AddFellowOakDicom();
builder.Services.AddSingleton<DicomRepository>();
builder.Services.AddSingleton<DicomServer>();

// 确保配置服务正确注册
builder.Services.Configure<DicomSettings>(builder.Configuration.GetSection("DicomSettings"));

var app = builder.Build();

// 获取服务
var dicomRepository = app.Services.GetRequiredService<DicomRepository>();

// 配置 DICOM
CStoreSCP.Configure(
    settings.StoragePath,
    settings.TempPath,
    settings,
    dicomRepository
);

// 启动 DICOM 服务器
var dicomServer = app.Services.GetRequiredService<DicomServer>();
await dicomServer.StartAsync();
app.Lifetime.ApplicationStopping.Register(() => dicomServer.StopAsync().GetAwaiter().GetResult());

// 优化线程池 - 基于CPU核心数
int processorCount = Environment.ProcessorCount;
ThreadPool.SetMinThreads(processorCount * 4, processorCount * 2);    // 增加最小线程数
ThreadPool.SetMaxThreads(processorCount * 8, processorCount * 4);    // 增加最大线程数

// 配置中间件
if (app.Environment.IsDevelopment() && settings.Swagger.Enabled)
{
    // Swagger 中间件应该在其他中间件之前
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", $"{settings.Swagger.Title} {settings.Swagger.Version}");
        // 可以设置为根路径
        c.RoutePrefix = "swagger";
    });
}

// 其他中间件
app.UseAuthorization();
app.MapControllers();

app.Run(); 