using System.ComponentModel.DataAnnotations;

namespace Monitor.Core.Options;

/// <summary>S3 bucket + credentials used by Monitor.Files for listing/search/download.</summary>
public sealed class AwsOptions
{
    public const string SectionName = "Aws";

    [Required(AllowEmptyStrings = false)]
    public string AccessKeyId { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string SecretAccessKey { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    [Required(AllowEmptyStrings = false)]
    public string S3Bucket { get; set; } = string.Empty;

    public string S3Prefix { get; set; } = "data/";
}
