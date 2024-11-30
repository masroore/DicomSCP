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

// 文件日志 - 统一记录到一个文件，添加服务标识
if (logSettings.EnableFileLog)
{
    logConfig.WriteTo.File(
        path: Path.Combine(logSettings.LogPath, "dicom-scp-.txt"),
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: logSettings.FileLogLevel,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
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
builder.Services.AddSingleton<WorklistRepository>();

// 确保配置服务正确注册
builder.Services.Configure<DicomSettings>(builder.Configuration.GetSection("DicomSettings"));

// 在 builder.Services 配置部分添加
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

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
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", $"{settings.Swagger.Title} {settings.Swagger.Version}");
        c.RoutePrefix = "swagger";
    });
}

app.UseStaticFiles();  // 先处理静态文件
app.UseRouting();     // 然后是路由

// 认证中间件
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower();

    // 1. 检查是否是 DICOM 实例请求
    if (path?.StartsWith("/api/images/instances/") == true)
    {
        // DICOM 实例请求直接放行，不检查认证
        await next();
        return;
    }

    // 2. 检查是否是白名单路径
    var allowedPaths = new[] 
    {
        "/login.html",
        "/api/auth/login",
        "/lib",
        "/css",
        "/js"
    };

    if (allowedPaths.Any(p => path?.StartsWith(p) == true))
    {
        await next();
        return;
    }

    // 3. 检查认证状态
    if (!context.Request.Cookies.ContainsKey("auth"))
    {
        // API 请求返回 401，而不是重定向
        if (path?.StartsWith("/api") == true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        // 只有页面请求才重定向
        context.Response.Redirect("/login.html");
        return;
    }

    await next();
});

app.UseAuthorization();
app.UseCors();
app.MapControllers();

// 处理根路径
app.MapGet("/", context =>
{
    context.Response.Redirect("/index.html");
    return Task.CompletedTask;
});

app.Run(); 