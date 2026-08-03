using Microsoft.Extensions.Logging;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Polls a freshly-started blue-green candidate's health endpoint until it is
/// ready to serve (tech-spec §7): <c>GET {address}{healthPath}</c> until it
/// answers 200 or the timeout elapses. Behind an interface so the blue-green
/// flow can be unit-tested without a real container or network.
/// </summary>
public interface IReadinessProber
{
    /// <summary>
    /// Poll <paramref name="address"/> + <paramref name="healthPath"/> until it
    /// returns HTTP 200 or <paramref name="timeout"/> elapses. Returns true if the
    /// candidate became ready, false on timeout. Never throws for an unreachable
    /// candidate — a failed poll is a false result, which aborts the swap.
    /// </summary>
    Task<bool> WaitForReadyAsync(
        string address,
        string healthPath,
        TimeSpan timeout,
        CancellationToken ct = default);
}

/// <summary>
/// Production prober over a named <see cref="HttpClient"/>. Polls at a fixed
/// interval, treating connection errors and non-200 responses as "not ready yet"
/// and retrying until the deadline.
/// </summary>
public sealed class HttpReadinessProber : IReadinessProber
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for readiness polls.</summary>
    public const string HttpClientName = "reconciler-readiness";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ReconcilerOptions _options;
    private readonly ILogger<HttpReadinessProber> _logger;

    public HttpReadinessProber(
        IHttpClientFactory httpClientFactory,
        ReconcilerOptions options,
        ILogger<HttpReadinessProber> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> WaitForReadyAsync(
        string address,
        string healthPath,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var url = $"{address.TrimEnd('/')}/{healthPath.TrimStart('/')}";
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        while (!deadlineCts.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync(url, deadlineCts.Token);
                if ((int)response.StatusCode == 200)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Candidate not up yet, or the deadline fired mid-request; keep polling.
            }

            try
            {
                await Task.Delay(_options.ReadinessPollInterval, deadlineCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogWarning("Readiness poll for {Url} did not reach 200 within {Timeout}", url, timeout);
        return false;
    }
}
