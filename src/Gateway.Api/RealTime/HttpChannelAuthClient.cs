using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.Api.Proxy;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// Production <see cref="IChannelAuthClient"/>: POSTs the auth callback to the owning
/// service and interprets its answer (task #594). The service is reached exactly the
/// way the health prober reaches it (tech-spec §4.1) — through
/// <see cref="IServiceAddressResolver"/> at the learned Docker-assigned host port from
/// <see cref="ServiceHostPortMap"/>, falling back to the manifest port when no host
/// port has been recorded yet — so there is a single service-address mechanism, not a
/// second one invented here.
/// <para>
/// The body is <c>{ channel, credential, connectionId }</c>; the only admit answer is
/// HTTP 200 with <c>{ "allow": true }</c> (optionally <c>"identity"</c>). Anything else
/// — non-200, a 200 with <c>allow:false</c> or a malformed body, a 2-second timeout, or
/// an unreachable service — is a deny. The gateway never inspects the credential.
/// </para>
/// </summary>
public sealed class HttpChannelAuthClient : IChannelAuthClient
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for auth callbacks.</summary>
    public const string HttpClientName = "channel-auth";

    /// <summary>Per-callback timeout (task #594: 2s). A slow app denies, it does not hang the hub.</summary>
    public static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceAddressResolver _addressResolver;
    private readonly ServiceHostPortMap? _hostPorts;
    private readonly ILogger<HttpChannelAuthClient> _logger;

    public HttpChannelAuthClient(
        IHttpClientFactory httpClientFactory,
        IServiceAddressResolver addressResolver,
        ILogger<HttpChannelAuthClient> logger,
        ServiceHostPortMap? hostPorts = null)
    {
        _httpClientFactory = httpClientFactory;
        _addressResolver = addressResolver;
        _hostPorts = hostPorts;
        _logger = logger;
    }

    public async Task<ChannelAuthDecision> AuthorizeAsync(
        ChannelOwner owner,
        string channel,
        string? credential,
        string connectionId,
        CancellationToken ct = default)
    {
        // Resolve the base address the same way HttpHealthProber does: the port the
        // container is actually bound to (a Docker-assigned host port), falling back to
        // the manifest port when container-truth is not available yet.
        var baseAddress = (_hostPorts is not null && _hostPorts.TryGet(owner.Service, out var hostPort)
            ? _addressResolver.Resolve(owner.Service, hostPort)
            : _addressResolver.Resolve(owner.Service, owner.Port)).TrimEnd('/');

        var path = owner.AuthPath!.StartsWith('/') ? owner.AuthPath : "/" + owner.AuthPath;
        var url = baseAddress + path;

        // Bound each callback independently at 2s, still honouring caller cancellation.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallbackTimeout);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.PostAsJsonAsync(
                url,
                new CallbackRequest(channel, credential, connectionId),
                JsonOptions,
                timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Channel auth callback for {Channel} to {Service} denied (HTTP {Status}).",
                    channel, owner.Service, (int)response.StatusCode);
                return ChannelAuthDecision.Deny;
            }

            var body = await response.Content.ReadFromJsonAsync<CallbackResponse>(JsonOptions, timeoutCts.Token);
            if (body is null || !body.Allow)
            {
                return ChannelAuthDecision.Deny;
            }

            return ChannelAuthDecision.Allow(body.Identity);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            // Timed out, unreachable, or a body the service should not have sent. Fail
            // closed: an app that cannot answer cleanly does not get its channel joined.
            _logger.LogInformation(
                ex, "Channel auth callback for {Channel} to {Service} failed; denying.", channel, owner.Service);
            return ChannelAuthDecision.Deny;
        }
    }

    private sealed record CallbackRequest(string Channel, string? Credential, string ConnectionId);

    private sealed record CallbackResponse(
        [property: JsonPropertyName("allow")] bool Allow,
        [property: JsonPropertyName("identity")] string? Identity);
}
