using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// The shared real-time hub (tech-spec §4.2). One hub, mapped at <c>/hub</c>, that
/// every downstream app can fan events out through so none has to implement its
/// own WebSocket handling. Clients subscribe to <c>{app}:{topic}</c> channels via
/// <see cref="JoinChannel"/> / <see cref="LeaveChannel"/>, which map onto SignalR
/// groups; a matching group broadcast (from <c>POST /internal/publish</c> or an
/// <see cref="IHubContext{THub}"/>) reaches every subscriber.
/// <para>
/// The hub does <b>no</b> end-user authentication (design invariant, §1): channels
/// are public broadcast. The sole exception is the dashboard's <c>ops:*</c>
/// channels, which require an authenticated connection. That check runs through
/// the <see cref="OpsChannelPolicy"/> authorization policy — a hook that is
/// satisfied by an authenticated principal today and becomes a real Cognito-JWT
/// gate in the auth task. Until then, anonymous <c>ops:*</c> joins are rejected.
/// </para>
/// </summary>
public sealed class GatewayHub : Hub
{
    /// <summary>Authorization policy gating <c>ops:*</c> channel joins.</summary>
    public const string OpsChannelPolicy = "OpsChannel";

    /// <summary>Prefix marking the authenticated dashboard channels.</summary>
    public const string OpsChannelPrefix = "ops:";

    /// <summary>
    /// The single message a delegated-auth denial reports. Deliberately generic: it
    /// leaks neither whether the channel exists nor why the join was refused (task #594).
    /// </summary>
    public const string AuthDeniedMessage = "Not authorized to join this channel.";

    private readonly IAuthorizationService _authorization;
    private readonly IChannelOwnershipResolver _ownership;
    private readonly IChannelAuthClient _authClient;
    private readonly ChannelAuthDecisionCache _decisions;
    private readonly ILogger<GatewayHub> _logger;

    public GatewayHub(
        IAuthorizationService authorization,
        IChannelOwnershipResolver ownership,
        IChannelAuthClient authClient,
        ChannelAuthDecisionCache decisions,
        ILogger<GatewayHub> logger)
    {
        _authorization = authorization;
        _ownership = ownership;
        _authClient = authClient;
        _decisions = decisions;
        _logger = logger;
    }

    /// <summary>
    /// Log every accepted connection with its id and authenticated user (or
    /// <c>anonymous</c>) at Information — no metrics infra, just a trail for
    /// correlating dashboard reconnects and the token-expiry closes.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation(
            "Hub connection {ConnectionId} established for {User}.",
            Context.ConnectionId,
            DescribeUser());
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Log every disconnect at Information, or at Warning with the exception when
    /// the connection dropped abnormally (transport fault, token-expiry close).
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            _logger.LogInformation(
                "Hub connection {ConnectionId} for {User} disconnected.",
                Context.ConnectionId,
                DescribeUser());
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Hub connection {ConnectionId} for {User} disconnected with an error.",
                Context.ConnectionId,
                DescribeUser());
        }

        // Drop every delegated-auth decision cached for this connection (task #594): a
        // reconnect gets a fresh connection id and must re-authorize from scratch.
        _decisions.Drop(Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>The connection's authenticated identity name, or <c>anonymous</c>.</summary>
    private string DescribeUser() =>
        Context.User?.Identity?.IsAuthenticated == true
            ? Context.User.Identity!.Name ?? "authenticated"
            : "anonymous";

    /// <summary>
    /// Subscribe the caller to a <c>{prefix}:{topic}</c> channel. The prefix must be
    /// either <c>ops</c> (gateway-owned, gated by the <see cref="OpsChannelPolicy"/>)
    /// or the name of an existing manifest service (task #593) — a channel whose
    /// prefix owns to no service is an anonymous free-for-all no longer, and the join
    /// is rejected.
    /// <para>
    /// This one-argument form carries no credential, so a join to a <b>private</b>
    /// channel (one whose owning service set <c>realtime_auth_path</c>, task #594) runs
    /// the delegated-auth callback with a null credential — join
    /// <see cref="JoinPrivateChannel"/> instead to present one. Public and <c>ops:*</c>
    /// channels are unaffected. Kept exactly one-argument so the dashboard's existing
    /// <c>JoinChannel(channel)</c> calls keep binding.
    /// </para>
    /// Throws <see cref="HubException"/> on an invalid channel name, an unauthorized
    /// <c>ops:*</c> join, an unowned prefix, or a denied delegated-auth callback.
    /// </summary>
    public Task JoinChannel(string channel) => JoinChannelAsync(channel, credential: null);

    /// <summary>
    /// Subscribe the caller to a private <c>{service}:{topic}</c> channel, presenting
    /// the service's own opaque <paramref name="credential"/> (task #594 — the
    /// Pusher/Ably auth-delegation pattern). The gateway never parses the credential; it
    /// forwards it verbatim on the auth callback to the owning service's
    /// <c>realtime_auth_path</c>, and admits the join only if that callback allows it.
    /// <para>
    /// This is the delegated-auth companion to <see cref="JoinChannel"/>. It is a
    /// separate hub method rather than an optional second parameter on
    /// <c>JoinChannel</c> because SignalR binds hub methods by exact argument count — it
    /// supports neither optional/variadic parameters nor same-name overloads — so a
    /// single method could not bind both the legacy one-argument call and a credentialed
    /// two-argument call. Both methods share one authorization/caching core; passing a
    /// null credential here is identical to calling <c>JoinChannel(channel)</c>.
    /// </para>
    /// Throws <see cref="HubException"/> under the same conditions as
    /// <see cref="JoinChannel"/>, including a denied auth callback.
    /// </summary>
    public Task JoinPrivateChannel(string channel, string? credential) =>
        JoinChannelAsync(channel, credential);

    private async Task JoinChannelAsync(string channel, string? credential)
    {
        ValidateChannel(channel);
        await AuthorizeChannelAsync(channel, credential);
        await Groups.AddToGroupAsync(Context.ConnectionId, channel);
    }

    /// <summary>Unsubscribe the caller from a channel.</summary>
    public async Task LeaveChannel(string channel)
    {
        ValidateChannel(channel);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, channel);
    }

    /// <summary>True for channel names in the authenticated <c>ops:*</c> namespace.</summary>
    public static bool IsOpsChannel(string channel) =>
        channel.StartsWith(OpsChannelPrefix, StringComparison.Ordinal);

    private async Task AuthorizeChannelAsync(string channel, string? credential)
    {
        if (IsOpsChannel(channel))
        {
            var user = Context.User ?? new ClaimsPrincipal(new ClaimsIdentity());
            var result = await _authorization.AuthorizeAsync(user, resource: null, OpsChannelPolicy);
            if (!result.Succeeded)
            {
                throw new HubException(
                    $"Channel '{channel}' requires an authenticated connection.");
            }

            return;
        }

        // Every non-ops channel must be owned by an existing manifest service (task
        // #593): the prefix is the service name. Reject a channel whose prefix owns to
        // no service — channels are no longer an anonymous free-for-all.
        var prefix = PrefixOf(channel);
        var owner = await _ownership.ResolveAsync(prefix);
        if (owner is null)
        {
            throw new HubException(
                $"Channel '{channel}' has no owning service; '{prefix}' is not a known service.");
        }

        // No auth path → the service's channels are public: join freely (as before #594).
        if (string.IsNullOrEmpty(owner.AuthPath))
        {
            return;
        }

        await AuthorizeDelegatedAsync(owner, channel, credential);
    }

    /// <summary>
    /// Delegated (private-channel) authorization (task #594): honour a decision already
    /// cached for this connection, else ask the owning service's auth callback and cache
    /// the result — an allow for the connection's lifetime, a deny only briefly. A deny
    /// (whether cached or fresh) throws a generic <see cref="HubException"/> that leaks
    /// nothing about why.
    /// </summary>
    private async Task AuthorizeDelegatedAsync(ChannelOwner owner, string channel, string? credential)
    {
        var connectionId = Context.ConnectionId;

        // A cached decision short-circuits: an allow admits any credential; a deny only
        // short-circuits a retry of the SAME credential, so a retry with a different,
        // now-valid credential still reaches the owning service (task #608 finding 1).
        if (_decisions.TryGet(connectionId, channel, credential) is { } cached)
        {
            if (cached.Allowed)
            {
                return;
            }

            throw new HubException(AuthDeniedMessage);
        }

        // Cap concurrent in-flight callbacks per connection at one: a join-loop cannot
        // hold multiple 2s downstream slots. If a callback is already running for this
        // connection, fail closed without caching — the in-flight one decides the join.
        if (!_decisions.TryBeginAuthCallback(connectionId))
        {
            throw new HubException(AuthDeniedMessage);
        }

        ChannelAuthDecision decision;
        try
        {
            decision = await _authClient.AuthorizeAsync(owner, channel, credential, connectionId, Context.ConnectionAborted);
        }
        finally
        {
            _decisions.EndAuthCallback(connectionId);
        }

        if (decision.Allowed)
        {
            // The identity (may be null) rides with the cached allow for phase 3 to use.
            _decisions.StoreAllow(connectionId, channel, decision.Identity);
            return;
        }

        // Key the deny on this credential so a later retry with a different one is not blocked.
        _decisions.StoreDeny(connectionId, channel, credential);
        throw new HubException(AuthDeniedMessage);
    }

    /// <summary>The channel's owner prefix — everything before the first <c>:</c>.</summary>
    public static string PrefixOf(string channel)
    {
        var separator = channel.IndexOf(':');
        return separator <= 0 ? channel : channel[..separator];
    }

    /// <summary>
    /// The single <c>{app}:{topic}</c> shape rule, shared by <see cref="ValidateChannel"/>
    /// (joins) and <c>POST /internal/publish</c> (task #608 finding 3) so a channel that
    /// can never be joined can never be published to either. Deliberately permissive — the
    /// hub is opt-in infrastructure, not an app-level validator — but a missing <c>:</c>
    /// separator or empty segment is rejected so a bad name can never collide with the
    /// <c>ops:</c> namespace check or broadcast into a permanently-empty group.
    /// </summary>
    public static bool IsValidChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return false;
        }

        var separator = channel.IndexOf(':');
        return separator > 0 && separator != channel.Length - 1;
    }

    /// <summary>Enforce the <c>{app}:{topic}</c> shape on a join, throwing on a bad name.</summary>
    private static void ValidateChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new HubException("Channel name is required.");
        }

        if (!IsValidChannel(channel))
        {
            throw new HubException(
                $"Channel '{channel}' must be in '{{app}}:{{topic}}' form.");
        }
    }
}
