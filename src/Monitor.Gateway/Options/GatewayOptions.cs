using System.ComponentModel.DataAnnotations;

namespace Monitor.Gateway.Options;

/// <summary>
/// Process-level options for the gateway itself (as opposed to the
/// "ReverseProxy" section, which is YARP's own config tree of
/// Routes/Clusters and is bound directly by <c>AddReverseProxy().LoadFromConfig</c>).
/// </summary>
/// <remarks>
/// The gateway did not exist in the Node source — it is a pure addition of
/// the strangler-fig migration strategy, introduced so the retained
/// React/Vite frontend has one stable origin to call
/// (<c>VITE_API_URL</c>) while individual route groups (/api/auth,
/// /api/s3, /api/monitor) are cut over from the legacy Express service to
/// Monitor.Api one bounded context at a time.
/// </remarks>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Port Kestrel binds to. This is the address the frontend's
    /// VITE_API_URL now points at, replacing the legacy backend's port.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 8080;
}
