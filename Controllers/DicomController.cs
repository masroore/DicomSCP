using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Data;
using System.Management;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DicomController : ControllerBase
{
    private readonly DicomServer _server;
    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;

    public DicomController(
        DicomServer server,
        IOptions<DicomSettings> settings,
        DicomRepository repository)
    {
        _server = server;
        _settings = settings.Value;
        _repository = repository;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var serverStatus = _server.GetServicesStatus();
        var process = Process.GetCurrentProcess();

        // Get process memory usage (MB)
        var processMemory = process.WorkingSet64 / 1024.0 / 1024.0;

        // Get system information
        double totalPhysicalMemory = 0;
        double availablePhysicalMemory = 0;
        string cpuModel = "Unknown";
        double cpuUsage = 0;

        try
        {
            if (OperatingSystem.IsWindows())
            {
            // Windows system memory information
            var performanceInfo = PerformanceInfo.GetPerformanceInfo();
            var physicalMemoryInBytes = performanceInfo.PhysicalTotal.ToInt64() * performanceInfo.PageSize.ToInt64();
            totalPhysicalMemory = physicalMemoryInBytes / 1024.0 / 1024.0;  // Convert to MB

            var availableMemoryInBytes = performanceInfo.PhysicalAvailable.ToInt64() * performanceInfo.PageSize.ToInt64();
            availablePhysicalMemory = availableMemoryInBytes / 1024.0 / 1024.0;  // Convert to MB

            // Windows CPU information
                #pragma warning disable CA1416
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
                cpuModel = searcher.Get()
                    .Cast<ManagementObject>()
                    .Select(obj => obj["Name"]?.ToString())
                    .FirstOrDefault() ?? "Unknown";
                #pragma warning restore CA1416

                // Windows CPU usage
                using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                counter.NextValue();
                Thread.Sleep(100);
                cpuUsage = counter.NextValue();
            }
            else if (OperatingSystem.IsLinux())
            {
                // Linux memory information
                var memInfo = System.IO.File.ReadAllLines("/proc/meminfo");
                foreach (var line in memInfo)
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        totalPhysicalMemory = ParseLinuxMemInfo(line) / 1024.0; // Convert to MB
                    }
                    else if (line.StartsWith("MemAvailable:"))
                    {
                        availablePhysicalMemory = ParseLinuxMemInfo(line) / 1024.0; // Convert to MB
                    }
                }

                // Linux CPU model
                cpuModel = System.IO.File.ReadAllLines("/proc/cpuinfo")
                    .FirstOrDefault(line => line.StartsWith("model name"))
                    ?.Split(':')
                    .LastOrDefault()
                    ?.Trim() ?? "Unknown";

                // Linux CPU usage
                var startCpu = System.IO.File.ReadAllText("/proc/stat")
                    .Split('\n')[0]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .Take(7)
                    .Select(x => long.Parse(x))
                    .ToArray();

                Thread.Sleep(100);

                var endCpu = System.IO.File.ReadAllText("/proc/stat")
                    .Split('\n')[0]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .Take(7)
                    .Select(x => long.Parse(x))
                    .ToArray();

                var startIdle = startCpu[3];
                var endIdle = endCpu[3];
                var startTotal = startCpu.Sum();
                var endTotal = endCpu.Sum();

                if (endTotal - startTotal > 0)
                {
                    cpuUsage = (1.0 - (endIdle - startIdle) / (double)(endTotal - startTotal)) * 100;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS system information (using sysctl command)
                totalPhysicalMemory = GetMacMemoryInfo();
                availablePhysicalMemory = totalPhysicalMemory - GetMacMemoryUsage();
                cpuModel = GetMacCpuInfo();
                cpuUsage = GetMacCpuUsage();
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "Failed to retrieve system information");
        }

        var usedPhysicalMemory = totalPhysicalMemory - availablePhysicalMemory;
        var memoryUsagePercent = totalPhysicalMemory > 0 ? (usedPhysicalMemory / totalPhysicalMemory) * 100 : 0;

        return Ok(new
        {
            store = new
            {
                aeTitle = _settings.AeTitle,
                port = _settings.StoreSCPPort,
                isRunning = serverStatus.Services.StoreScp
            },
            worklist = new
            {
                aeTitle = _settings.WorklistSCP.AeTitle,
                port = _settings.WorklistSCP.Port,
                isRunning = serverStatus.Services.WorklistScp
            },
            qr = new
            {
                aeTitle = _settings.QRSCP.AeTitle,
                port = _settings.QRSCP.Port,
                isRunning = serverStatus.Services.QrScp
            },
            print = new
            {
                aeTitle = _settings.PrintSCP.AeTitle,
                port = _settings.PrintSCP.Port,
                isRunning = serverStatus.Services.PrintScp
            },
            system = new
            {
                cpuUsage = Math.Round(cpuUsage, 2),
                cpuModel = cpuModel,
                processMemory = Math.Round(processMemory, 2),
                systemMemoryTotal = Math.Round(totalPhysicalMemory, 2),
                systemMemoryUsed = Math.Round(usedPhysicalMemory, 2),
                systemMemoryPercent = Math.Round(memoryUsagePercent, 2),
                processorCount = Environment.ProcessorCount,
                processStartTime = new
                {
                    days = (DateTime.Now - process.StartTime).Days,
                    hours = (DateTime.Now - process.StartTime).Hours,
                    minutes = (DateTime.Now - process.StartTime).Minutes
                },
                osVersion = RuntimeInformation.OSDescription,
                platform = GetPlatformName()
            }
        });
    }

    private string GetPlatformName()
    {
        if (OperatingSystem.IsWindows())
        {
            return $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor} ({RuntimeInformation.OSArchitecture})";
        }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var osRelease = System.IO.File.ReadAllLines("/etc/os-release")
                    .ToDictionary(
                        line => line.Split('=')[0],
                        line => line.Split('=')[1].Trim('"')
                    );
                return $"{osRelease["PRETTY_NAME"]} ({RuntimeInformation.OSArchitecture})";
            }
            catch
            {
                return $"Linux ({RuntimeInformation.OSArchitecture})";
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            return $"macOS {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})";
        }
        return "Unknown";
    }

    private double ParseLinuxMemInfo(string line)
    {
        return double.Parse(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[1]);
    }

    private double GetMacMemoryInfo()
    {
        try
        {
            var output = ExecuteCommand("sysctl", "hw.memsize");
            var memSize = long.Parse(output.Split(':')[1].Trim());
            return memSize / 1024.0 / 1024.0; // Convert to MB
        }
        catch
        {
            return 0;
        }
    }

    private double GetMacMemoryUsage()
    {
        try
        {
            var output = ExecuteCommand("vm_stat", "");
            // Parse vm_stat output to get memory usage
            // ... specific implementation omitted
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private string GetMacCpuInfo()
    {
        try
        {
            return ExecuteCommand("sysctl", "-n machdep.cpu.brand_string").Trim();
        }
        catch
        {
            return "Unknown";
        }
    }

    private double GetMacCpuUsage()
    {
        try
        {
            var output = ExecuteCommand("top", "-l 1 -n 0");
            // Parse top output to get CPU usage
            // ... specific implementation omitted
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private string ExecuteCommand(string command, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        return process.StandardOutput.ReadToEnd();
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        try
        {
            if (_server.IsRunning)
            {
                DicomLogger.Warning("Api",
                    "[API] Failed to start service - Reason: {Reason}",
                    "Server is already running");
                return BadRequest(new
                {
                    Message = "Server is already running",
                    AeTitle = _settings.AeTitle,
                    StoreSCPPort = _settings.StoreSCPPort,
                    WorklistSCPPort = _settings.WorklistSCP.Port
                });
            }

            CStoreSCP.Configure(_settings, _repository);

            await _server.StartAsync();
            DicomLogger.Information("Api",
                "[API] Service started successfully");
            return Ok(new
            {
                Message = "Server started",
                AeTitle = _settings.AeTitle,
                StoreSCPPort = _settings.StoreSCPPort,
                WorklistSCPPort = _settings.WorklistSCP.Port
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex,
                "[API] Exception occurred while starting service");
            return StatusCode(500, "Failed to start server");
        }
    }
    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        try
        {
            if (!_server.IsRunning)
            {
                DicomLogger.Warning("Api",
                    "[API] Failed to stop service - Reason: {Reason}",
                    "Server is not running");
                return BadRequest("Server is not running");
            }

            await _server.StopAsync();
            DicomLogger.Information("Api",
                "[API] Service stopped successfully");
            return Ok("Server has stopped");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex,
                "[API] Exception occurred while stopping service");
            return StatusCode(500, "Failed to stop server");
        }
    }

    [HttpPost("restart")]
    public async Task<IActionResult> Restart()
    {
        try
        {
            DicomLogger.Information("Api", "[API] Restarting DICOM service...");
            await _server.RestartAllServices();
            DicomLogger.Information("Api", "[API] DICOM service restarted successfully");

            return Ok(new
            {
                Message = "Service restarted successfully",
                AeTitle = _settings.AeTitle,
                StoreSCPPort = _settings.StoreSCPPort,
                WorklistSCPPort = _settings.WorklistSCP.Port,
                QRSCPPort = _settings.QRSCP.Port
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] Failed to restart DICOM service");
            return StatusCode(500, "Failed to restart service");
        }
    }
}
[StructLayout(LayoutKind.Sequential)]
public struct PerformanceInfo
{
    public int Size;
    public IntPtr CommitTotal;
    public IntPtr CommitLimit;
    public IntPtr CommitPeak;
    public IntPtr PhysicalTotal;
    public IntPtr PhysicalAvailable;
    public IntPtr SystemCache;
    public IntPtr KernelTotal;
    public IntPtr KernelPaged;
    public IntPtr KernelNonpaged;
    public IntPtr PageSize;
    public int HandlesCount;
    public int ProcessCount;
    public int ThreadCount;

    public static PerformanceInfo GetPerformanceInfo()
    {
        var pi = new PerformanceInfo { Size = Marshal.SizeOf<PerformanceInfo>() };
        GetPerformanceInfo(out pi, pi.Size);
        return pi;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInfo PerformanceInformation, int Size);
}
