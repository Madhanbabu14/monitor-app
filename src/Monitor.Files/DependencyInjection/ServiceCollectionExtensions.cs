using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;

namespace Monitor.Files.DependencyInjection;

/// <summary>
/// Registers the Monitor.Files bounded context: the S3 client (analogue of
/// `new S3Client({ region, credentials })` in features/s3/s3.service.ts) and
/// <see cref="IFilesService"/>. Called once from Monitor.Api's Program.cs, after
/// <c>AddValidatedOptions&lt;AwsOptions&gt;</c> has registered the validated options
/// tree this depends on.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorFiles(this IServiceCollection services)
    {
        services.AddMemoryCache();

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var aws = sp.GetRequiredService<IOptions<AwsOptions>>().Value;
            var credentials = new BasicAWSCredentials(aws.AccessKeyId, aws.SecretAccessKey);
            var config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(aws.Region) };
            return new AmazonS3Client(credentials, config);
        });

        services.AddSingleton<IFilesService, FilesService>();

        return services;
    }
}
