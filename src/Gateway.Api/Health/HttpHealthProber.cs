using System.Diagnostics;
using Gateway.Api.Data;
using Gateway.Api.Proxy;
using Gateway.Api.Reconcile;

namespace Gateway.Api.Health;

/// <summary>
/// Production <see cref="IHealthProber"/>: issues <c>GET {base}/api/health</c>
/// over a named <see cref="HttpClient"/>, where <c>{base}</c> comes from
/// <see cref="IServiceAddressResolver"/> (<c>http://127.0.0.1:{port}</c>, the
/// host-published container port). Each probe is bounded by a 3-second timeout and any
/// failure is reported as an unreachable (<c>down</c>) result rather than thrown,
/// so a single sick service never fails the gateway's own health response.
/// </summary>
public sealed class HttpHealthProber : IHealthProber
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for probes.</summary>
    public const string HttpClientName = "health-prober";

    /// <summary>Per-service probe timeout (tech-spec: 3s).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceAddressResolver _addressResolver;
    private readonly ServiceHostPortMap? _hostPorts;
    private readonly ReconcilerOptions? _reconcilerOptions;

    public HttpHealthProber(
        IHttpClientFactory httpClientFactory,
        IServiceAddressResolver addressResolver,
        ServiceHostPortMap? hostPorts = null,
        ReconcilerOptions? reconcilerOptions = null)
    {
        _httpClientFactory = httpClientFactory;
        _addressResolver = addressResolver;
        _hostPorts = hostPorts;
        _reconcilerOptions = reconcilerOptions;
    }

    public async Task<ServiceProbeResult> ProbeAsync(ServiceManifest manifest, CancellationToken ct = default)
    {
        // Probe the port the container is actually bound to (a Docker-assigned host
        // port; a promoted-green container keeps the port it started on). In
        // reconciler mode we NEVER fall back to the manifest port: it is a
        // container-internal port whose host counterpart could belong to a
        // completely different service (incident 2026-08-17). Reporting the
        // service down here matches what the route now serves (503) so the
        // dashboard, the load balancer's read of /api/health, and reality agree.
        string baseAddress;
        if (_hostPorts is not null && _hostPorts.TryGet(manifest.Name, out var hostPort))
        {
            baseAddress = _addressResolver.Resolve(manifest.Name, hostPort);
        }
        else if (_reconcilerOptions?.Enabled == true)
        {
            return ServiceProbeResult.Unreachable();
        }
        else
        {
            // Proxy-only dev mode: the manifest port is the host the developer runs
            // the app on locally (README, "Proxy-only dev mode"); preserve it.
            baseAddress = _addressResolver.Resolve(manifest);
        }

        var url = $"{baseAddress.TrimEnd('/')}/api/health";

        // Bound each probe independently at 3s, still honouring caller cancellation.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, timeoutCts.Token);
            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            var httpStatus = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? ServiceProbeResult.Up(httpStatus, elapsedMs)
                : ServiceProbeResult.Down(httpStatus, elapsedMs);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Timed out or the service could not be reached. Do not rethrow: an
            // unhealthy downstream must not fail the gateway's aggregate.
            return ServiceProbeResult.Unreachable();
        }
    }
}
