using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Monitor.UnitTests.Files.Fakes;

/// <summary>
/// Test double for <see cref="IAmazonS3"/>. Hand-rolled rather than a mocking-library
/// proxy (none is referenced by this test project) by subclassing the real
/// <see cref="AmazonS3Client"/> — its constructor never touches the network, and
/// <see cref="AmazonS3Client.ListObjectsV2Async"/> / <see cref="AmazonS3Client.GetObjectAsync(GetObjectRequest, System.Threading.CancellationToken)"/>
/// are both non-sealed virtual members, so overriding just those two reproduces
/// everything <see cref="Monitor.Files.FilesService"/> actually calls without having to
/// hand-implement the ~140 other members of <see cref="IAmazonS3"/>.
/// </summary>
public class FakeAmazonS3Client : AmazonS3Client
{
    public FakeAmazonS3Client()
        : base(new BasicAWSCredentials("fake-access-key", "fake-secret-key"), new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1 })
    {
    }

    /// <summary>Queue of responses/exceptions returned in order, one per call to <see cref="ListObjectsV2Async"/>.</summary>
    public Queue<Func<ListObjectsV2Request, ListObjectsV2Response>> ListObjectsV2Responses { get; } = new();

    /// <summary>When set, every call to <see cref="ListObjectsV2Async"/> throws this instead of dequeuing a response.</summary>
    public Exception? ListObjectsV2Exception { get; set; }

    public List<ListObjectsV2Request> ListObjectsV2Calls { get; } = new();

    public Func<GetObjectRequest, GetObjectResponse>? GetObjectResponseFactory { get; set; }

    public Exception? GetObjectException { get; set; }

    public List<GetObjectRequest> GetObjectCalls { get; } = new();

    public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
    {
        ListObjectsV2Calls.Add(request);

        if (ListObjectsV2Exception is not null)
        {
            throw ListObjectsV2Exception;
        }

        if (ListObjectsV2Responses.Count == 0)
        {
            throw new InvalidOperationException("FakeAmazonS3Client: no queued ListObjectsV2Response left.");
        }

        return Task.FromResult(ListObjectsV2Responses.Dequeue()(request));
    }

    public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
    {
        GetObjectCalls.Add(request);

        if (GetObjectException is not null)
        {
            throw GetObjectException;
        }

        if (GetObjectResponseFactory is null)
        {
            throw new InvalidOperationException("FakeAmazonS3Client: GetObjectResponseFactory not configured.");
        }

        return Task.FromResult(GetObjectResponseFactory(request));
    }
}
