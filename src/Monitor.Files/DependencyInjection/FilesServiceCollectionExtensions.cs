using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;

namespace Monitor.Files.DependencyInjection;

/// <summary>
/// Registers the Monitor.Files bounded context: the S3 client (direct
/// analogue of s3.service.ts's module-level <c>new S3Client({ region,
/// credentials })</c>), the raw-listing memory cache, and
/// <see cref="IS3FilesService"/> itself. Called once from Monitor.Api's
/// Program.cs, after <c>AddValidatedOptions&lt;AwsOptions&gt;</c> and
/// <c>AddMonitorData()</c> (depends on <see cref="Monitor.Data.Repositories.IPrimaryDb"/>).
/// </summary>
public static class FilesServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorFiles(this IServiceCollection services)
    {
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AwsOptions>>().Value;
            var credentials = new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey);
            var config = new AmazonS3Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region),
            };
            return new AmazonS3Client(credentials, config);
        });

        services.AddMemoryCache();
        services.AddSingleton<S3ErrorMapper>();
        services.AddScoped<IS3FilesService, S3FilesService>();

        return services;
    }
}
