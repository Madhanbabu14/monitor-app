using Amazon.S3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Core.Errors;
using Monitor.Core.Options;

namespace Monitor.Files;

/// <summary>
/// Direct translation of the source's <c>toAppError</c> (s3.service.ts): maps
/// AWS S3 client failures onto the same <see cref="AppException"/> status
/// codes/messages the Node <c>@aws-sdk/client-s3</c> error names produced.
/// </summary>
public sealed class S3ErrorMapper
{
    private readonly ILogger<S3ErrorMapper> _logger;
    private readonly AwsOptions _awsOptions;

    public S3ErrorMapper(ILogger<S3ErrorMapper> logger, IOptions<AwsOptions> awsOptions)
    {
        _logger = logger;
        _awsOptions = awsOptions.Value;
    }

    public AppException ToAppException(Exception err)
    {
        var name = (err as AmazonS3Exception)?.ErrorCode ?? err.GetType().Name;
        var message = err.Message ?? "Unknown S3 error";
        int? httpStatus = err is AmazonS3Exception s3Ex ? (int)s3Ex.StatusCode : null;

        _logger.LogError(err, "S3 error {ErrorName} {Message} {HttpStatus}", name, message, httpStatus);

        if (name == "NoSuchBucket")
        {
            return new AppException(404, $"S3 bucket '{_awsOptions.S3Bucket}' not found. Verify AWS_S3_BUCKET and AWS_REGION in .env");
        }
        if (name == "NoSuchKey")
        {
            return new AppException(404, "File not found in S3");
        }
        if (name is "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidClientTokenId"
            || httpStatus == 403)
        {
            return new AppException(
                403,
                "S3 access denied. Check: AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_REGION, and that the IAM user has s3:ListBucket + s3:GetObject on this bucket.");
        }
        if (name is "RequestExpired" or "TokenExpiredError")
        {
            return new AppException(403, "AWS credentials have expired. Refresh your access keys.");
        }

        var lowerMessage = message.ToLowerInvariant();
        if (lowerMessage.Contains("econnrefused") || lowerMessage.Contains("etimedout") || lowerMessage.Contains("enotfound")
            || lowerMessage.Contains("connection refused") || lowerMessage.Contains("timed out") || lowerMessage.Contains("no such host"))
        {
            return new AppException(503, "Cannot reach AWS S3. Check network / VPC settings.");
        }

        return new AppException(
            httpStatus is >= 400 and < 600 ? httpStatus.Value : 500,
            $"S3 error ({(!string.IsNullOrEmpty(name) ? name : (object?)httpStatus ?? "unknown")}): {message}");
    }
}
