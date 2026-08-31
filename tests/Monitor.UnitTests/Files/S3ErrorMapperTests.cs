using System.Net;
using Amazon.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;
using Monitor.Files;

namespace Monitor.UnitTests.Files;

/// <summary>
/// Exercises <see cref="S3ErrorMapper"/> — the direct translation of the
/// source's <c>toAppError</c> (s3.service.ts), which maps AWS S3 error names
/// onto specific HTTP status codes/messages.
/// </summary>
public class S3ErrorMapperTests
{
    private static S3ErrorMapper CreateMapper(string bucket = "my-bucket") =>
        new(NullLogger<S3ErrorMapper>.Instance, Microsoft.Extensions.Options.Options.Create(new AwsOptions { S3Bucket = bucket }));

    [Fact]
    public void NoSuchBucket_Maps404WithBucketNameInMessage()
    {
        var mapper = CreateMapper("my-bucket");
        var err = new AmazonS3Exception("not found") { ErrorCode = "NoSuchBucket" };

        var result = mapper.ToAppException(err);

        Assert.Equal(404, result.StatusCode);
        Assert.Contains("my-bucket", result.Message);
    }

    [Fact]
    public void NoSuchKey_Maps404()
    {
        var mapper = CreateMapper();
        var err = new AmazonS3Exception("missing") { ErrorCode = "NoSuchKey" };

        var result = mapper.ToAppException(err);

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("File not found in S3", result.Message);
    }

    [Theory]
    [InlineData("AccessDenied")]
    [InlineData("InvalidAccessKeyId")]
    [InlineData("SignatureDoesNotMatch")]
    [InlineData("InvalidClientTokenId")]
    public void CredentialErrors_Map403(string errorCode)
    {
        var mapper = CreateMapper();
        var err = new AmazonS3Exception("denied") { ErrorCode = errorCode };

        var result = mapper.ToAppException(err);

        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public void HttpStatus403_Maps403EvenWithUnknownErrorCode()
    {
        var mapper = CreateMapper();
        var err = new AmazonS3Exception("denied") { ErrorCode = "SomethingElse", StatusCode = HttpStatusCode.Forbidden };

        var result = mapper.ToAppException(err);

        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public void RequestExpired_Maps403WithExpiredCredentialsMessage()
    {
        var mapper = CreateMapper();
        var err = new AmazonS3Exception("expired") { ErrorCode = "RequestExpired" };

        var result = mapper.ToAppException(err);

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("AWS credentials have expired. Refresh your access keys.", result.Message);
    }

    [Fact]
    public void ConnectionRefusedMessage_Maps503()
    {
        var mapper = CreateMapper();
        var err = new Exception("connect ECONNREFUSED 127.0.0.1:443");

        var result = mapper.ToAppException(err);

        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public void UnknownError_FallsBackTo500()
    {
        var mapper = CreateMapper();
        var err = new Exception("boom");

        var result = mapper.ToAppException(err);

        Assert.Equal(500, result.StatusCode);
    }
}
