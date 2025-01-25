using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;
using Serilog;
using Serilog.Events;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Data;
using Microsoft.OpenApi.Models;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Rewrite;

var builder = WebApplication.CreateBuilder(args);

// Configure console (cross-platform support)
if (Environment.UserInteractive)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            // Disable quick edit mode on Windows
            var handle = ConsoleHelper.GetStdHandle(-10);
            if (handle != IntPtr.Zero && ConsoleHelper.GetConsoleMode(handle, out uint mode))
            {
                mode &= ~(uint)(0x0040 | 0x0010);
                ConsoleHelper.SetConsoleMode(handle, mode);
            }
        }
        else
        {
            // Unix/Linux/MacOS settings
            Console.TreatControlCAsInput = true;
        }
    }
    catch
    {
        // Ignore console configuration errors
    }
}

// Register encoding provider
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Register DICOM encoding
DicomEncoding.RegisterEncoding("GB2312", "GB2312");

// Configure Kestrel
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
    options.Limits.MaxConcurrentConnections = 100;
    options.Limits.MaxConcurrentUpgradedConnections = 100;
    options.Limits.MaxRequestBodySize = 52428800; // 50MB
});

// Get configuration
var settings = builder.Configuration.GetSection("DicomSettings").Get<DicomSettings>()
    ?? new DicomSettings();
// Get Swagger configuration
var swaggerSettings = builder.Configuration.GetSection("Swagger").Get<SwaggerSettings>()
    ?? new SwaggerSettings();

// Configure logging
var logSettings = builder.Configuration
    .GetSection("Logging")
    .Get<LogSettings>() ?? new LogSettings();

// Initialize DICOM logging
DicomLogger.Initialize(logSettings);

// Initialize database logging
BaseRepository.ConfigureLogging(logSettings);

// Initialize API logging
ApiLoggingMiddleware.ConfigureLogging(logSettings);

// Configure framework logging
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Warning()  // Log only warnings and above
    .Filter.ByExcluding(e =>
        e.Properties.ContainsKey("SourceContext") &&
        e.Properties["SourceContext"].ToString().Contains("FellowOakDicom.Network") &&
        (e.MessageTemplate.Text.Contains("No accepted presentation context found") ||
         e.MessageTemplate.Text.Contains("Study Root Query/Retrieve Information Model - FIND") ||
         e.MessageTemplate.Text.Contains("Patient Root Query/Retrieve Information Model - FIND") ||
         e.MessageTemplate.Text.Contains("Storage Commitment Push Model SOP Class") ||
         e.MessageTemplate.Text.Contains("Modality Performed Procedure Step") ||
         e.MessageTemplate.Text.Contains("Basic Grayscale Print Management Meta") ||
         e.MessageTemplate.Text.Contains("Basic Color Print Management Meta") ||
         e.MessageTemplate.Text.Contains("Verification SOP Class") ||
         e.MessageTemplate.Text.Contains("rejected association") ||
         e.MessageTemplate.Text.Contains("Association received")))
    .WriteTo.Logger(lc => lc
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:l}{NewLine}",
            restrictedToMinimumLevel: LogEventLevel.Warning
        )
    );

Log.Logger = logConfig.CreateLogger();
builder.Host.UseSerilog();

// Add logging services
builder.Services.AddLogging(loggingBuilder =>
    loggingBuilder.AddSerilog(dispose: true));

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Configure Swagger
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

// Register DICOM services
builder.Services
    .AddFellowOakDicom()
    .AddTranscoderManager<NativeTranscoderManager>();

builder.Services.AddSingleton<DicomRepository>();
builder.Services.AddSingleton<DicomServer>();
builder.Services.AddSingleton<WorklistRepository>();
builder.Services.AddSingleton<IStoreSCU, StoreSCU>();
builder.Services.AddSingleton<IPrintSCU, PrintSCU>();

// Ensure configuration services are registered correctly
builder.Services.Configure<DicomSettings>(builder.Configuration.GetSection("DicomSettings"));
builder.Services.Configure<QueryRetrieveConfig>(builder.Configuration.GetSection("QueryRetrieveConfig"));
builder.Services.AddScoped<IQueryRetrieveSCU, QueryRetrieveSCU>();
// Register Swagger configuration
builder.Services.Configure<SwaggerSettings>(builder.Configuration.GetSection("Swagger"));

builder.Services.AddAuthentication("CustomAuth")
    .AddCookie("CustomAuth", options =>
    {
        options.Cookie.Name = "auth";
        options.LoginPath = "/login.html";
        // Cookie expiration time
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;  // Disable default sliding expiration
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        // Enable max age, otherwise it will be the request timestamp + 30 minutes.
        // If set to more than 30 minutes, each request will update the expiration time, causing inaccurate expiration time.
        // options.Cookie.MaxAge = TimeSpan.FromHours(12);

        // Add validation ticket expiration handling
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            // Modify validation handling
            OnValidatePrincipal = async context =>
            {
                // Check if expired
                if (context.Properties?.ExpiresUtc <= DateTimeOffset.UtcNow)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync("CustomAuth");
                    return;
                }

                // If not status endpoint, manually update expiration time
                if (!context.HttpContext.Request.Path.StartsWithSegments("/api/dicom/status") &&
                    context.Properties is not null)
                {
                    context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.Add(options.ExpireTimeSpan);
                    context.ShouldRenew = true;
                }
            }
        };
    });

// Add authorization without setting default policy
builder.Services.AddAuthorization();

// Configure forwarded headers
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                              Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    // Clear default networks, otherwise they will be ignored due to security checks
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add CORS policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
        });
});

// Configure URL rewrite rules
var rewriteOptions = new RewriteOptions()
    // Rewrite non-direct file access under dicomviewer path to dicomviewer/index.html to solve SPA single application routing issue
    .AddRewrite(
        @"^dicomviewer/(?!.*\.(js|css|png|jpe?g|gif|ico|svg|woff2?|ttf|otf|eot|map|json|mp[34]|webm|mkv|avi|mov|pdf|docx?|xlsx?|pptx?|zip|rar|tar|gz|7z|ts|sh|bat|py|xml|ya?ml|ini|wasm|aac)).*$",
        "/dicomviewer/index.html",
        skipRemainingRules: true
    );

var app = builder.Build();

// Initialize service provider
DicomServiceProvider.Initialize(app.Services);

// Get services
var dicomRepository = app.Services.GetRequiredService<DicomRepository>();

// Configure DICOM
DicomSetupBuilder.UseServiceProvider(app.Services);

CStoreSCP.Configure(settings, dicomRepository);

// Start DICOM server
var dicomServer = app.Services.GetRequiredService<DicomServer>();
await dicomServer.StartAsync();
app.Lifetime.ApplicationStopping.Register(() => dicomServer.StopAsync().GetAwaiter().GetResult());

// Optimize thread pool - based on CPU core count
int processorCount = Environment.ProcessorCount;
ThreadPool.SetMinThreads(processorCount * 4, processorCount * 2);    // Minimum thread count
ThreadPool.SetMaxThreads(processorCount * 8, processorCount * 4);    // Maximum thread count

// 1. Forwarded headers middleware (first)
app.UseForwardedHeaders();

// 2. API logging middleware
app.UseMiddleware<ApiLoggingMiddleware>();

// 3. URL rewrite (before static files)
app.UseRewriter(rewriteOptions);

// 4. Static file handling
app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html", "login.html" }
});
app.UseStaticFiles();

// 5. Swagger
if (swaggerSettings.Enabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 6. Routing and authentication
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseCors("AllowAll");  // CORS should be here

// 7. API authentication middleware
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower();

    // Check authentication only for API paths
    if (path?.StartsWith("/api/") == true &&
        !path.StartsWith("/api/auth/login") &&
        !context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.StatusCode = 401;
        return;
    }

    await next();
});

// 8. Controllers
app.MapControllers();

// 9. Root path handling
app.MapGet("/", context =>
{
    if (!context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.Redirect("/login.html");
    }
    else
    {
        context.Response.Redirect("/index.html");
    }
    return Task.CompletedTask;
});

app.Run();

// Windows console API definitions
internal static class ConsoleHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
