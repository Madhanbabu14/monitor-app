namespace Monitor.Files.Domain;

/// <summary>
/// Everything the <c>GET /api/s3/download</c> endpoint needs to stream the S3
/// object back to the caller — the .NET split of the source's <c>streamFile</c>
/// (s3.service.ts), which set response headers and piped the body directly.
/// Keeping the response-writing (headers, HttpContext) in Monitor.Api and only
/// the S3 round-trip here matches the layering brief: "endpoint ... DTO
/// mapping live together; the service keeps the business logic."
/// </summary>
public sealed class S3FileDownload : IAsyncDisposable
{
    public Stream Content { get; }
    public string? ContentType { get; }
    public long? ContentLength { get; }
    public string FileName { get; }

    public S3FileDownload(Stream content, string? contentType, long? contentLength, string fileName)
    {
        Content = content;
        ContentType = contentType;
        ContentLength = contentLength;
        FileName = fileName;
    }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
