namespace Monitor.Files.Domain;

/// <summary>
/// Carries what the endpoint needs to reproduce <c>S3Controller.downloadFile</c> /
/// <c>S3Service.streamFile</c>'s response headers + body without the service reaching
/// into <see cref="Microsoft.AspNetCore.Http.HttpResponse"/> directly.
/// </summary>
public sealed record S3DownloadResult(Stream Body, string ContentType, long? ContentLength, string FileName);
