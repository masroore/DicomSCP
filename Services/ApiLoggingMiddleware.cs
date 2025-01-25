using System.Diagnostics;
using DicomSCP.Configuration;
using Serilog.Events;

namespace DicomSCP.Services;

public class ApiLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public ApiLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public static void ConfigureLogging(LogSettings settings)
    {
        // No longer need separate logging configuration, use DicomLogger
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
            sw.Stop();

            // Log successful request
            DicomLogger.Information("Api",
                "[API] Request succeeded - {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Log failed request
            DicomLogger.Error("Api", ex,
                "[API] Request failed - {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
            throw;
        }
        finally
        {
            // Log request details
            var statusCode = context.Response.StatusCode;
            var level = statusCode >= 500 ? LogEventLevel.Error :
                       statusCode >= 400 ? LogEventLevel.Warning :
                       LogEventLevel.Information;

            if (level == LogEventLevel.Error)
            {
                DicomLogger.Error("Api", null,
                    "[API] Request details - {Method} {Path} - {StatusCode} - {ElapsedMs:0.0000}ms",
                    context.Request.Method,
                    context.Request.Path,
                    statusCode,
                    sw.Elapsed.TotalMilliseconds);
            }
            else if (level == LogEventLevel.Warning)
            {
                DicomLogger.Warning("Api",
                    "[API] Request details - {Method} {Path} - {StatusCode} - {ElapsedMs:0.0000}ms",
                    context.Request.Method,
                    context.Request.Path,
                    statusCode,
                    sw.Elapsed.TotalMilliseconds);
            }
            else
            {
                DicomLogger.Information("Api",
                    "[API] Request details - {Method} {Path} - {StatusCode} - {ElapsedMs:0.0000}ms",
                    context.Request.Method,
                    context.Request.Path,
                    statusCode,
                    sw.Elapsed.TotalMilliseconds);
            }
        }
    }
}
