using Serilog;
using DicomSCP.Configuration;
using ILogger = Serilog.ILogger;

namespace DicomSCP.Services;

/// <summary>
/// Unified DICOM logging service
/// </summary>
public static class DicomLogger
{
    private static readonly Dictionary<string, ILogger> _loggers = new();
    private static LogSettings? _settings;
    private static ILogger? _logger;

    private const string DefaultConsoleTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
    private const string DefaultFileTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Initialize logging service
    /// </summary>
    public static void Initialize(LogSettings settings)
    {
        _settings = settings;

        // Configure DicomServer logging (output to console only)
        var serverLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _loggers["DicomServer"] = serverLogger;

        // Configure QR service logging
        if (settings.Services.QRSCP?.Enabled == true)
        {
            var qrConfig = settings.Services.QRSCP;
            var qrLogger = CreateLogger("QRSCP", qrConfig);
            _loggers["QRSCP"] = qrLogger;
        }

        // Configure QueryRetrieveSCU logging
        if (settings.Services.QueryRetrieveSCU.Enabled)
        {
            var qrConfig = settings.Services.QueryRetrieveSCU;
            var qrLogger = CreateLogger("QueryRetrieveSCU", qrConfig);
            _loggers["QueryRetrieveSCU"] = qrLogger;
        }

        // Configure WorklistSCP logging
        if (settings.Services.WorklistSCP?.Enabled == true)
        {
            var worklistConfig = settings.Services.WorklistSCP;
            var worklistLogger = CreateLogger("WorklistSCP", worklistConfig);
            _loggers["WorklistSCP"] = worklistLogger;
        }

        // Configure StoreSCP logging
        if (settings.Services.StoreSCP?.Enabled == true)
        {
            var storeConfig = settings.Services.StoreSCP;
            var storeLogger = CreateLogger("StoreSCP", storeConfig);
            _loggers["StoreSCP"] = storeLogger;
        }

        // Configure StoreSCU logging
        if (settings.Services.StoreSCU?.Enabled == true)
        {
            var storeSCUConfig = settings.Services.StoreSCU;
            var storeSCULogger = CreateLogger("StoreSCU", storeSCUConfig);
            _loggers["StoreSCU"] = storeSCULogger;
        }

        // Configure PrintSCP logging
        if (settings.Services.PrintSCP?.Enabled == true)
        {
            var printConfig = settings.Services.PrintSCP;
            var printLogger = CreateLogger("PrintSCP", printConfig);
            _loggers["PrintSCP"] = printLogger;
        }

        // Configure PrintSCU logging
        if (settings.Services.PrintSCU?.Enabled == true)
        {
            var printScuConfig = settings.Services.PrintSCU;
            var printScuLogger = CreateLogger("PrintSCU", printScuConfig);
            _loggers["PrintSCU"] = printScuLogger;
        }

        // Configure database logging
        if (settings.Database.Enabled)
        {
            var dbConfig = settings.Database;
            var dbLogger = new LoggerConfiguration()
                .MinimumLevel.Is(dbConfig.MinimumLevel);

            if (dbConfig.EnableConsoleLog)
            {
                dbLogger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");
            }

            if (dbConfig.EnableFileLog)
            {
                Directory.CreateDirectory(dbConfig.LogPath);
                dbLogger.WriteTo.File(
                    path: Path.Combine(dbConfig.LogPath, "database-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: settings.RetainedDays);
            }

            _loggers["Database"] = dbLogger.CreateLogger();
        }

        // Configure API logging
        if (settings.Api.Enabled)
        {
            var apiConfig = settings.Api;
            var apiLogger = new LoggerConfiguration()
                .MinimumLevel.Is(apiConfig.MinimumLevel);

            if (apiConfig.EnableConsoleLog)
            {
                apiLogger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");
            }

            if (apiConfig.EnableFileLog)
            {
                Directory.CreateDirectory(apiConfig.LogPath);
                apiLogger.WriteTo.File(
                    path: Path.Combine(apiConfig.LogPath, "api-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: settings.RetainedDays);
            }

            _loggers["Api"] = apiLogger.CreateLogger();
        }

        // Configure WADO service logging
        if (settings.Services.WADO?.Enabled == true)
        {
            var wadoConfig = settings.Services.WADO;
            var wadoLogger = new LoggerConfiguration()
                .MinimumLevel.Is(wadoConfig.MinimumLevel);

            if (wadoConfig.EnableConsoleLog)
            {
                wadoLogger.WriteTo.Console(
                    outputTemplate: string.IsNullOrEmpty(wadoConfig.OutputTemplate)
                        ? (_settings?.OutputTemplate ?? DefaultConsoleTemplate)
                        : wadoConfig.OutputTemplate);
            }

            if (wadoConfig.EnableFileLog)
            {
                var logPath = string.IsNullOrEmpty(wadoConfig.LogPath)
                    ? Path.Combine(_settings?.LogPath ?? "logs", "wado")
                    : wadoConfig.LogPath;

                Directory.CreateDirectory(logPath);

                wadoLogger.WriteTo.File(
                    path: Path.Combine(logPath, "wado-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: string.IsNullOrEmpty(wadoConfig.OutputTemplate)
                        ? (_settings?.OutputTemplate ?? DefaultFileTemplate)
                        : wadoConfig.OutputTemplate,
                    retainedFileCountLimit: _settings?.RetainedDays ?? 31);
            }

            _loggers["WADO"] = wadoLogger.CreateLogger();

        }

        // Set default logger
        _logger = serverLogger;
        Log.Logger = serverLogger;
    }

    private static ILogger CreateLogger(string serviceName, ServiceLogConfig config)
    {
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(config.MinimumLevel);

        if (config.EnableConsoleLog)
        {
            logConfig.WriteTo.Console(
                outputTemplate: string.IsNullOrEmpty(config.OutputTemplate)
                    ? (_settings?.OutputTemplate ?? DefaultConsoleTemplate)
                    : config.OutputTemplate);
        }

        if (config.EnableFileLog)
        {
            var logPath = string.IsNullOrEmpty(config.LogPath)
                ? Path.Combine(_settings?.LogPath ?? "logs", serviceName.ToLower())
                : config.LogPath;

            // Ensure log directory exists
            Directory.CreateDirectory(logPath);

            logConfig.WriteTo.File(
                path: Path.Combine(logPath, $"{serviceName.ToLower()}-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: string.IsNullOrEmpty(config.OutputTemplate)
                    ? (_settings?.OutputTemplate ?? DefaultFileTemplate)
                    : config.OutputTemplate,
                retainedFileCountLimit: _settings?.RetainedDays ?? 31);
        }

        return logConfig.CreateLogger();
    }

    private static ILogger GetLogger(string service = "DicomServer")
    {
        if (_loggers.TryGetValue(service, out var logger))
            return logger;

        // If a specific service logger is not found, return the default logger
        return _logger ?? CreateDefaultLogger();
    }

    private static ILogger CreateDefaultLogger()
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: DefaultConsoleTemplate)
            .CreateLogger();

        _logger = logger;
        return logger;
    }

    public static void Debug(string messageTemplate, params object[] propertyValues)
        => GetLogger("DicomServer").Debug(messageTemplate, propertyValues);

    public static void Information(string messageTemplate, params object[] propertyValues)
        => GetLogger("DicomServer").Information(messageTemplate, propertyValues);

    public static void Warning(string messageTemplate, params object[] propertyValues)
        => GetLogger("DicomServer").Warning(messageTemplate, propertyValues);

    public static void Error(string messageTemplate, params object[] propertyValues)
        => GetLogger("DicomServer").Error(messageTemplate, propertyValues);

    public static void Error(Exception? exception, string messageTemplate, params object[] propertyValues)
        => GetLogger("DicomServer").Error(exception, messageTemplate, propertyValues);

    public static void Debug(string service, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Debug(messageTemplate, propertyValues);

    public static void Information(string service, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Information(messageTemplate, propertyValues);

    public static void Warning(string service, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Warning(messageTemplate, propertyValues);

    public static void Error(string service, Exception? exception, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Error(exception, messageTemplate, propertyValues);

    public static void Error(string service, string messageTemplate, params object[] propertyValues)
        => GetLogger(service).Error(messageTemplate, propertyValues);

    /// <summary>
    /// Close logging service
    /// </summary>
    public static void CloseAndFlush()
    {
        foreach (var logger in _loggers.Values)
        {
            (logger as IDisposable)?.Dispose();
        }
        _loggers.Clear();
        Log.CloseAndFlush();
    }
}
