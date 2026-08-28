using System.Net;
using System.Net.Sockets;
using Amazon.S3;
using Microsoft.Extensions.Logging;
using Monitor.Core.Errors;

namespace Monitor.Files;

/// <summary>
/// Direct translation of <c>toAppError</c> (features/s3/s3.service.ts): maps AWS SDK
/// failures to the same status codes / operator-facing messages the source produced
/// from the JS SDK v3's error <c>name</c> / <c>$metadata.httpStatusCode</c>. The source's
/// <c>TokenExpiredError</c> branch (a jsonwebtoken error name, not an S3 SDK error) is not
/// ported — it could never actually fire from <c>fetchAllObjects</c> / <c>streamFile</c> /
/// <c>getPipelineNames</c>, all of which only ever throw S3Client errors.
/// </summary>
internal static class S3ErrorMapper
{
    public static AppException ToAppException(Exception err, ILogger logger, string s3Bucket)
    {
        var errorCode = (err as AmazonS3Exception)?.ErrorCode ?? string.Empty;
        var message = err.Message;
        var httpStatus = (err as AmazonS3Exception)?.StatusCode is HttpStatusCode s ? (int)s : (int?)null;

        logger.LogError(err, "S3 error: errorCode={ErrorCode} httpStatus={HttpStatus}", errorCode, httpStatus);

        if (errorCode == "NoSuchBucket")
        {
            return new AppException(404, $"S3 bucket '{s3Bucket}' not found. Verify AWS_S3_BUCKET and AWS_REGION in .env");
        }

        if (errorCode == "NoSuchKey")
        {
            return new AppException(404, "File not found in S3");
        }

        if (errorCode is "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidClientTokenId" || httpStatus == 403)
        {
            return new AppException(
                403,
                "S3 access denied. Check: AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_REGION, and that the IAM user has s3:ListBucket + s3:GetObject on this bucket.");
        }

        if (errorCode == "RequestExpired")
        {
            return new AppException(403, "AWS credentials have expired. Refresh your access keys.");
        }

        if (IsConnectivityFailure(err))
        {
            return new AppException(503, "Cannot reach AWS S3. Check network / VPC settings.");
        }

        return new AppException(
            httpStatus is >= 400 and < 600 ? httpStatus.Value : 500,
            $"S3 error ({(!string.IsNullOrEmpty(errorCode) ? errorCode : httpStatus?.ToString() ?? "unknown")}): {message}");
    }

    // Source checked message.toLowerCase() for 'econnrefused' / 'etimedout' / 'enotfound'
    // (Node's libuv error codes). The .NET AWS SDK surfaces the equivalent failures as a
    // SocketException (possibly wrapped) or a message mentioning the same conditions.
    private static bool IsConnectivityFailure(Exception err)
    {
        for (var e = err; e is not null; e = e.InnerException)
        {
            if (e is SocketException)
            {
                return true;
            }

            var lower = e.Message.ToLowerInvariant();
            if (lower.Contains("connection refused") || lower.Contains("timed out") ||
                lower.Contains("name or service not known") || lower.Contains("no such host"))
            {
                return true;
            }
        }

        return false;
    }
}
