using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Data;
using DicomSCP.Models;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// 获取配置
var settings = builder.Configuration.GetSection("DicomSettings").Get<DicomSettings>() 
    ?? new DicomSettings();

// 配置日志
var logSettings = builder.Configuration
    .GetSection("Logging")
    .Get<LogSettings>() ?? new LogSettings();

// 初始化DICOM日志
DicomLogger.Initialize(logSettings);

// 初始化数据库日志
BaseRepository.ConfigureLogging(logSettings);

// 初始化API日志
ApiLoggingMiddleware.ConfigureLogging(logSettings);

// 配置全局日志（用于其他服务）
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
builder.Services.Configure<QueryRetrieveConfig>(builder.Configuration.GetSection("QueryRetrieveConfig"));
builder.Services.AddScoped<IQueryRetrieveSCU, QueryRetrieveSCU>();

var app = builder.Build();

// 添加API日志中间件
app.UseMiddleware<ApiLoggingMiddleware>();

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

// 认证中间件
app.Use(async (context, next) =>
{
    var allowedPaths = new[] 
    {
        "/login.html",
        "/api/auth/login",
        "/lib",
        "/css",
        "/js"
    };

    var path = context.Request.Path.Value?.ToLower();

    if (allowedPaths.Any(p => path?.StartsWith(p) == true))
    {
        await next();
        return;
    }

    // 检查认证 Cookie
    if (!context.Request.Cookies.ContainsKey("auth"))
    {
        if (path?.StartsWith("/api") == true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.Response.Redirect("/login.html");
        return;
    }

    await next();
});

// 其他中间件按顺序放在认证中间件后面
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapControllers();

// 处理根路径
app.MapGet("/", context =>
{
    context.Response.Redirect("/index.html");
    return Task.CompletedTask;
});

app.Run(); 