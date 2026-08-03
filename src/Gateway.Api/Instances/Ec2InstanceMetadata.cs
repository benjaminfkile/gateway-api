namespace Gateway.Api.Instances;

/// <summary>
/// Reads instance identity from the EC2 Instance Metadata Service using
/// <b>IMDSv2</b> (tech-spec §3, §6): first PUT a short-lived session token, then
/// GET the metadata items with that token. Every call is bounded by a small
/// timeout (default 2s) so that off-EC2 — where the link-local address just hangs
/// — this resolves quickly to "unavailable" and the caller falls back to the
/// environment source.
/// </summary>
public sealed class Ec2InstanceMetadata : IInstanceMetadata
{
    /// <summary>Named <see cref="HttpClient"/> used for IMDS calls.</summary>
    public const string HttpClientName = "imds";

    /// <summary>IMDS link-local base address.</summary>
    public const string ImdsBaseAddress = "http://169.254.169.254/";

    private const string TokenPath = "latest/api/token";
    private const string TokenTtlHeader = "X-aws-ec2-metadata-token-ttl-seconds";
    private const string TokenHeader = "X-aws-ec2-metadata-token";

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public Ec2InstanceMetadata(HttpClient http)
        : this(http, TimeSpan.FromSeconds(2))
    {
    }

    public Ec2InstanceMetadata(HttpClient http, TimeSpan timeout)
    {
        _http = http;
        _timeout = timeout;
    }

    public async Task<InstanceIdentity?> TryResolveAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await FetchTokenAsync(ct);
            if (token is null)
            {
                return null;
            }

            var instanceId = await FetchMetadataAsync("latest/meta-data/instance-id", token, ct);
            if (string.IsNullOrEmpty(instanceId))
            {
                // Reachable but no instance id — treat as not-an-EC2-instance.
                return null;
            }

            var privateIp = await FetchMetadataAsync("latest/meta-data/local-ipv4", token, ct);
            var publicIp = await FetchMetadataAsync("latest/meta-data/public-ipv4", token, ct);

            return new InstanceIdentity(
                instanceId,
                string.IsNullOrEmpty(privateIp) ? null : privateIp,
                string.IsNullOrEmpty(publicIp) ? null : publicIp);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Unreachable / timed out — not on EC2 (or IMDS disabled). Fall back.
            return null;
        }
    }

    private async Task<string?> FetchTokenAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, TokenPath);
        request.Headers.TryAddWithoutValidation(TokenTtlHeader, "60");

        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var token = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    private async Task<string?> FetchMetadataAsync(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(TokenHeader, token);

        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // 404 for e.g. public-ipv4 on an instance without one — just absent.
            return null;
        }

        return (await response.Content.ReadAsStringAsync(ct)).Trim();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is not null && !request.RequestUri.IsAbsoluteUri && _http.BaseAddress is null)
        {
            request.RequestUri = new Uri(new Uri(ImdsBaseAddress), request.RequestUri);
        }

        // Bound every call independently so a hung link-local address cannot stall
        // startup; 2s per the spec.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
    }
}
