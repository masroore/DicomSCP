using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Imaging.NativeCodec;
using FellowOakDicom.Imaging.Codec;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Data;
using DicomSCP.Models;
using Microsoft.OpenApi.Models;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// 添加线程池配置
ThreadPool.SetMinThreads(100, 100); // 设置最小工作线程和 I/O 线程数

// 注册编码提供程序
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// 注册 DICOM 编码
DicomEncoding.RegisterEncoding("GB2312", "GB2312");

// 配置 Kestrel
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
    options.Limits.MaxConcurrentConnections = 100;
    options.Limits.MaxConcurrentUpgradedConnections = 100;
    options.Limits.MaxRequestBodySize = 52428800; // 50MB
});

// 获取配置
var settings = builder.Configuration.GetSection("DicomSettings").Get<DicomSettings>() 
    ?? new DicomSettings();
// 获取 Swagger 配置
var swaggerSettings = builder.Configuration.GetSection("Swagger").Get<SwaggerSettings>()
    ?? new SwaggerSettings();

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

// 配置框架日志
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Warning()  // 只记录警告以上的日志
    .WriteTo.Logger(lc => lc
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:l}{NewLine}",
            restrictedToMinimumLevel: LogEventLevel.Warning
        )
    );

Log.Logger = logConfig.CreateLogger();
builder.Host.UseSerilog();

// 添加日志服务
builder.Services.AddLogging(loggingBuilder =>
    loggingBuilder.AddSerilog(dispose: true));

// 添加服务
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// 配置 Swagger
if (swaggerSettings.Enabled)
{
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc(swaggerSettings.Version, new OpenApiInfo 
        { 
            Title = swaggerSettings.Title,
            Version = swaggerSettings.Version,
            Description = swaggerSettings.Description
        });
    });
}

// DICOM服务注册
builder.Services
    .AddFellowOakDicom()
    .AddTranscoderManager<NativeTranscoderManager>();

builder.Services.AddSingleton<DicomRepository>();
builder.Services.AddSingleton<DicomServer>();
builder.Services.AddSingleton<WorklistRepository>();
builder.Services.AddSingleton<IStoreSCU, StoreSCU>();
builder.Services.AddSingleton<IPrintSCU, PrintSCU>();

// 确保配置服务正确注册
builder.Services.Configure<DicomSettings>(builder.Configuration.GetSection("DicomSettings"));
builder.Services.Configure<QueryRetrieveConfig>(builder.Configuration.GetSection("QueryRetrieveConfig"));
builder.Services.AddScoped<IQueryRetrieveSCU, QueryRetrieveSCU>();
// 注册 Swagger 配置
builder.Services.Configure<SwaggerSettings>(builder.Configuration.GetSection("Swagger"));

var app = builder.Build();

// 初始化服务提供者
DicomServiceProvider.Initialize(app.Services);

// 添加API日志中间件
app.UseMiddleware<ApiLoggingMiddleware>();

// 获取服务
var dicomRepository = app.Services.GetRequiredService<DicomRepository>();

// 配置 DICOM
DicomSetupBuilder.UseServiceProvider(app.Services);

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
if (app.Environment.IsDevelopment() && swaggerSettings.Enabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", $"{swaggerSettings.Title} {swaggerSettings.Version}");
        c.RoutePrefix = "swagger";
    });
}

// 配置静态文件
app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html", "login.html" }
});
app.UseStaticFiles();

// 认证中间件
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower();
    
    var allowedPaths = new[] 
    {
        "/login.html",
        "/api/auth/login",
        "/lib",
        "/css",
        "/js",
        "/favicon.ico"
    };

    // 记录请求路径和认证信息
    DicomLogger.Debug("Api", "[Auth] Request Path: {0}", path ?? "(null)");
    DicomLogger.Debug("Api", "[Auth] Has Auth Cookie: {0}", context.Request.Cookies.ContainsKey("auth").ToString());

    if (allowedPaths.Any(p => path?.StartsWith(p) == true))
    {
        DicomLogger.Debug("Api", "[Auth] Allowed path: {0}", path ?? "(null)");
        await next();
        return;
    }

    // 检查认证 Cookie
    if (!context.Request.Cookies.ContainsKey("auth"))
    {
        DicomLogger.Warning("Api", "[Auth] No auth cookie found for path: {0}", path ?? "(null)");
        if (path?.StartsWith("/api") == true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        
        var scheme = context.Request.Scheme;
        var host = context.Request.Host.Value;
        var redirectUrl = $"{scheme}://{host}/login.html";
        context.Response.Redirect(redirectUrl);
        return;
    }

    // 验证 Cookie 值
    var authCookie = context.Request.Cookies["auth"];
    if (string.IsNullOrEmpty(authCookie))
    {
        DicomLogger.Warning("Api", "[Auth] Auth cookie is empty");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    DicomLogger.Debug("Api", "[Auth] Auth cookie found: {0}", authCookie ?? "(null)");
    await next();
});

// 其他中间件按顺序放在认证中间件后面
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