using DicomSCP.Configuration;
using DicomSCP.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DicomSCP.Services;

public static class DicomServiceProvider
{
    private static IServiceProvider? _serviceProvider;

    public static void Initialize(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public static T GetRequiredService<T>() where T : class
    {
        if (_serviceProvider == null)
        {
            throw new InvalidOperationException("Service provider is not initialized");
        }

        var service = _serviceProvider.GetService<T>();
        if (service == null)
        {
            throw new InvalidOperationException($"Required service {typeof(T).Name} is not available");
        }

        return service;
    }

    public static T? GetOptionalService<T>() where T : class
    {
        return _serviceProvider?.GetService<T>();
    }
}