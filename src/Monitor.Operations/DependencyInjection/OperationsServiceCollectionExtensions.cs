using Microsoft.Extensions.DependencyInjection;

namespace Monitor.Operations.DependencyInjection;

/// <summary>
/// Registers the Monitor.Operations bounded context (monitor.service.ts's
/// singleton <c>monitorService</c>) for DI. Depends on Monitor.Data's
/// <c>IOperationalDb</c> (registered by <c>AddMonitorData()</c>) and
/// Monitor.Files's <c>IS3FilesService</c> (registered by
/// <c>AddMonitorFiles()</c>) — both must be called first in Program.cs.
/// </summary>
public static class OperationsServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorOperations(this IServiceCollection services)
    {
        services.AddScoped<IMonitorService, MonitorService>();
        return services;
    }
}
