using FellowOakDicom;
using Serilog;
using DicomSCP.Configuration;
using DicomSCP.Services;

var builder = WebApplication.CreateBuilder(args);

// 配置日志
builder.Host.UseSerilog((context, config) => config
    .WriteTo.Console()
    .WriteTo.File("logs/dicom-scp-.txt", rollingInterval: RollingInterval.Day));

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

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run(); 