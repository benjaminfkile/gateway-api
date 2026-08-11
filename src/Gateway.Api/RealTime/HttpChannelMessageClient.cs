using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Proxy;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// Production <see cref="IChannelMessageClient"/>: POSTs a client-originated message to the
/// owning service's <c>realtime_message_path</c> (task #611). The service is reached exactly
/// the way the auth callback (<see cref="HttpChannelAuthClient"/>) and the health prober
/// reach it — through <see cref="IServiceAddressResolver"/> at the learned Docker-assigned
/// host port from <see cref="ServiceHostPortMap"/>, falling back to the manifest port when
/// no host port has been recorded yet — so there is a single service-address mechanism.
/// <para>
/// The body is <c>{ channel, event, data, connectionId, identity }</c>. The owner's
/// response body is <b>ignored</b> (fire-and-forget toward the client): a <c>2xx</c> means
/// delivery was accepted; anything else — non-2xx, a 5-second timeout, or an unreachable
/// service — is logged at Warning and returned as <c>false</c> so the hub can throw an
/// error back to the sender. The gateway never broadcasts the message; if the owner wants
/// fan-out it publishes via <c>/internal/publish</c>.
/// </para>
/// </summary>
public sealed class HttpChannelMessageClient : IChannelMessageClient
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for message forwards.</summary>
    public const string HttpClientName = "channel-message";

    /// <summary>Per-forward timeout (task #611: 5s). A slow app fails delivery, it does not hang the hub.</summary>
    public static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceAddressResolver _addressResolver;
    private readonly ServiceHostPortMap? _hostPorts;
    private readonly ILogger<HttpChannelMessageClient> _logger;

    public HttpChannelMessageClient(
        IHttpClientFactory httpClientFactory,
        IServiceAddressResolver addressResolver,
        ILogger<HttpChannelMessageClient> logger,
        ServiceHostPortMap? hostPorts = null)
    {
        _httpClientFactory = httpClientFactory;
        _addressResolver = addressResolver;
        _hostPorts = hostPorts;
        _logger = logger;
    }

    public async Task<bool> ForwardAsync(
        ChannelOwner owner,
        string channel,
        string @event,
        object? data,
        string connectionId,
        string? identity,
        CancellationToken ct = default)
    {
        // Resolve the base address the same way the auth callback does: the port the
        // container is actually bound to (a Docker-assigned host port), falling back to
        // the manifest port when container-truth is not available yet.
        var baseAddress = (_hostPorts is not null && _hostPorts.TryGet(owner.Service, out var hostPort)
            ? _addressResolver.Resolve(owner.Service, hostPort)
            : _addressResolver.Resolve(owner.Service, owner.Port)).TrimEnd('/');

        var path = owner.MessagePath!.StartsWith('/') ? owner.MessagePath : "/" + owner.MessagePath;
        var url = baseAddress + path;

        // Bound each forward independently at 5s, still honouring caller cancellation.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ForwardTimeout);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.PostAsJsonAsync(
                url,
                new ForwardRequest(channel, @event, data, connectionId, identity),
                JsonOptions,
                timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Realtime message forward for {Channel} to {Service} failed (HTTP {Status}).",
                    channel, owner.Service, (int)response.StatusCode);
                return false;
            }

            // The owner's response body is deliberately ignored (fire-and-forget).
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Timed out or unreachable: the sender is told delivery failed.
            _logger.LogWarning(
                ex, "Realtime message forward for {Channel} to {Service} failed; delivery not accepted.",
                channel, owner.Service);
            return false;
        }
    }

    private sealed record ForwardRequest(
        string Channel, string Event, object? Data, string ConnectionId, string? Identity);
}
