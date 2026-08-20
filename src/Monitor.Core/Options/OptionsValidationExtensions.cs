using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Monitor.Core.Options;

/// <summary>
/// Single place every bounded-context project uses to bind + validate its
/// options tree. Replaces the Node config module's `required(key)` /
/// `optional(key, default)` helpers: missing required values now fail fast
/// at host startup (ValidateOnStart) instead of throwing the first time a
/// route touches `config.*`.
/// </summary>
public static class OptionsValidationExtensions
{
    public static IServiceCollection AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        services
            .AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }
}
