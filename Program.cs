using FellowOakDicom;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using DicomSCP.Configuration;
using DicomSCP.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSwaggerGen();
builder.Services.Configure<DicomSettings>(builder.Configuration.GetSection("DicomSettings"));
builder.Services.AddSingleton<DicomServer>();

// 配置 DICOM
var settings = builder.Configuration.GetSection("DicomSettings").Get<DicomSettings>() 
    ?? new DicomSettings();
CStoreSCP.Configure(settings.StoragePath);

// 优化线程池
ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount);
ThreadPool.SetMaxThreads(Environment.ProcessorCount * 4, Environment.ProcessorCount * 2);

var app = builder.Build();

// 启动 DICOM 服务器
var dicomServer = app.Services.GetRequiredService<DicomServer>();
await dicomServer.StartAsync();
app.Lifetime.ApplicationStopping.Register(() => dicomServer.StopAsync().GetAwaiter().GetResult());

// 配置中间件
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

app.Run(); 