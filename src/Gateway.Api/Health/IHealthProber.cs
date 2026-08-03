using Gateway.Api.Data;

namespace Gateway.Api.Health;

/// <summary>
/// Result of probing a single downstream service's <c>/api/health</c> endpoint.
/// <paramref name="Status"/> is <c>"up"</c> when the service answered with a 2xx
/// status, otherwise <c>"down"</c>. <paramref name="HttpStatus"/> and
/// <paramref name="ResponseTimeMs"/> are populated when an HTTP response was
/// received and are <c>null</c> when the probe could not complete (timeout,
/// connection refused, DNS failure, …). A <c>down</c> service never fails the
/// gateway's own health response (tech-spec §4.1).
/// </summary>
public sealed record ServiceProbeResult(string Status, int? HttpStatus, long? ResponseTimeMs)
{
    public static ServiceProbeResult Up(int httpStatus, long responseTimeMs) =>
        new("up", httpStatus, responseTimeMs);

    /// <summary>A service that answered with a non-2xx HTTP status.</summary>
    public static ServiceProbeResult Down(int httpStatus, long responseTimeMs) =>
        new("down", httpStatus, responseTimeMs);

    /// <summary>A service that could not be reached at all (timeout/error).</summary>
    public static ServiceProbeResult Unreachable() => new("down", null, null);
}

/// <summary>
/// Probes a downstream service's health endpoint. Behind an interface so tests
/// can supply deterministic results without a real network, while production
/// uses <see cref="HttpHealthProber"/> over a named <see cref="HttpClient"/>.
/// </summary>
public interface IHealthProber
{
    /// <summary>
    /// Probe <c>GET http://{name}:{port}/api/health</c> for the given service.
    /// Must never throw for an unreachable service — it returns a <c>down</c>
    /// result instead, so one bad service cannot take down the aggregate.
    /// </summary>
    Task<ServiceProbeResult> ProbeAsync(ServiceManifest manifest, CancellationToken ct = default);
}
