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
    /// Reused by <c>SendToChannel</c> when the caller is not a member of the target
    /// channel (task #611, rule a) — the same "not authorized" answer.
    /// </summary>
    public const string AuthDeniedMessage = "Not authorized to join this channel.";

    /// <summary>
    /// <c>SendToChannel</c> rejection when the owning service has no
    /// <c>realtime_message_path</c> configured (task #611, rule b): full-duplex is opt-in
    /// per service, so this is a distinct, clear error rather than the generic denial.
    /// </summary>
    public const string MessagingNotEnabledMessage =
        "This channel's service does not accept client messages (no realtime_message_path configured).";

    /// <summary>
    /// <c>SendToChannel</c> rejection when the connection is over its per-connection
    /// message rate budget (task #611, rule c).
    /// </summary>
    public const string MessageRateLimitedMessage =
        "Message rate limit exceeded; slow down and retry.";

    /// <summary>
    /// <c>SendToChannel</c> rejection when the owning service did not accept delivery —
    /// a non-2xx response or a timeout on the message forward (task #611, item 3). The
    /// gateway never broadcasts the message, so the sender is told delivery failed.
    /// </summary>
    public const string DeliveryFailedMessage =
        "The channel's service did not accept the message.";

    private readonly IAuthorizationService _authorization;
    private readonly IChannelOwnershipResolver _ownership;
    private readonly IChannelAuthClient _authClient;
    private readonly ChannelAuthDecisionCache _decisions;
    private readonly HubChannelMembership _membership;
    private readonly MessageRateLimiter _messageRateLimiter;
    private readonly IChannelMessageClient _messageClient;
    private readonly IPresenceRegistry _presence;
    private readonly PresenceEventCoalescer _presenceEvents;
    private readonly ILogger<GatewayHub> _logger;

    public GatewayHub(
        IAuthorizationService authorization,
        IChannelOwnershipResolver ownership,
        IChannelAuthClient authClient,
        ChannelAuthDecisionCache decisions,
        HubChannelMembership membership,
        MessageRateLimiter messageRateLimiter,
        IChannelMessageClient messageClient,
        IPresenceRegistry presence,
        PresenceEventCoalescer presenceEvents,
        ILogger<GatewayHub> logger)
    {
        _authorization = authorization;
        _ownership = ownership;
        _authClient = authClient;
        _decisions = decisions;
        _membership = membership;
        _messageRateLimiter = messageRateLimiter;
        _messageClient = messageClient;
        _presence = presence;
        _presenceEvents = presenceEvents;
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
        // reconnect gets a fresh connection id and must re-authorize from scratch. Also
        // drop its channel memberships and its message-rate bucket (task #611) so neither
        // side-registry pins memory for a connection id that will never be seen again.
        _decisions.Drop(Context.ConnectionId);
        _membership.Drop(Context.ConnectionId);
        _messageRateLimiter.Drop(Context.ConnectionId);

        // Presence disconnect sweep (task #612): remove the connection from every channel it
        // was present in and record a leave delta on each so a coalesced presence event
        // reflects the departure. Best-effort — a registry failure must never fail teardown.
        await SweepPresenceAsync(Context.ConnectionId);

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
        var identity = await AuthorizeChannelAsync(channel, credential);
        await Groups.AddToGroupAsync(Context.ConnectionId, channel);

        // Record the membership (and the auth-callback identity, null for public/ops) so
        // SendToChannel can enforce "must be a member" locally and stamp the forward with
        // who sent it (task #611). Record AFTER the group add so a failed add never leaves
        // a phantom membership.
        _membership.Join(Context.ConnectionId, channel, identity);

        // Presence (task #612): add to the workload-agnostic registry and buffer a join
        // delta for a coalesced presence event. Best-effort — presence never fails a join.
        await AddPresenceAsync(channel, identity);
    }

    /// <summary>Unsubscribe the caller from a channel.</summary>
    public async Task LeaveChannel(string channel)
    {
        ValidateChannel(channel);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, channel);
        _membership.Leave(Context.ConnectionId, channel);

        // Presence (task #612): drop the registry row and buffer a leave delta. Best-effort.
        await RemovePresenceAsync(channel);
    }

    /// <summary>
    /// Send a message FROM the caller's client TO the channel's owning service (task #611 —
    /// full-duplex). The gateway forwards it to the owner's <c>realtime_message_path</c> and
    /// never broadcasts it itself; if the owner wants fan-out it publishes via
    /// <c>/internal/publish</c>. Rules are enforced in order:
    /// <list type="number">
    /// <item>the connection must currently be a member of <paramref name="channel"/>
    /// (it joined and was not evicted) — else the generic <see cref="AuthDeniedMessage"/>;</item>
    /// <item>the owning service must have <c>realtime_message_path</c> configured (opt-in) —
    /// else the distinct <see cref="MessagingNotEnabledMessage"/>;</item>
    /// <item>the per-connection message rate budget must not be exceeded (token bucket,
    /// shared across all the connection's channels) — else <see cref="MessageRateLimitedMessage"/>;</item>
    /// <item>the payload is capped by SignalR's 32 KB receive limit — no extra check here.</item>
    /// </list>
    /// The owner's response body is ignored (fire-and-forget toward the client); a non-2xx
    /// or timeout on the forward is surfaced to the caller as <see cref="DeliveryFailedMessage"/>
    /// so the sender knows delivery failed. Throws <see cref="HubException"/> on any rejection.
    /// </summary>
    public async Task SendToChannel(string channel, string @event, object? data)
    {
        ValidateChannel(channel);

        // (a) Membership: only a current member of the channel may send to it. The identity
        // recorded at join time (null for public/ops) rides along on the forward.
        if (!_membership.TryGetIdentity(Context.ConnectionId, channel, out var identity))
        {
            throw new HubException(AuthDeniedMessage);
        }

        // (b) Opt-in: the owning service must have a message path. A reserved/ops prefix
        // resolves to no owner, so ops:* sends fall here too — the distinct clear error.
        var prefix = PrefixOf(channel);
        var owner = await _ownership.ResolveAsync(prefix, Context.ConnectionAborted);
        if (owner is null || string.IsNullOrEmpty(owner.MessagePath))
        {
            throw new HubException(MessagingNotEnabledMessage);
        }

        // (c) Per-connection message rate limit (shared across every channel).
        if (!_messageRateLimiter.TryTake(Context.ConnectionId))
        {
            throw new HubException(MessageRateLimitedMessage);
        }

        // Forward to the owner's message path. The response body is ignored; the hub
        // method returns as soon as the forward is accepted (2xx). A non-2xx or timeout
        // is surfaced to the sender so it knows delivery failed — the gateway never
        // broadcasts on the client's behalf.
        var delivered = await _messageClient.ForwardAsync(
            owner, channel, @event, data, Context.ConnectionId, identity, Context.ConnectionAborted);
        if (!delivered)
        {
            throw new HubException(DeliveryFailedMessage);
        }
    }

    /// <summary>
    /// Add the connection to the presence registry and buffer a coalesced join event
    /// (task #612). Best-effort: a registry failure is logged and swallowed so presence,
    /// which is advisory, can never fail an already-authorized join.
    /// </summary>
    private async Task AddPresenceAsync(string channel, string? identity)
    {
        try
        {
            await _presence.AddAsync(channel, Context.ConnectionId, identity, Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Presence add failed for connection {ConnectionId} on channel '{Channel}'; continuing.",
                Context.ConnectionId, channel);
        }

        // The coalescer buffer is in-memory and never throws; record after the registry
        // attempt so a departed connection is reflected even if the registry write faulted.
        _presenceEvents.RecordJoin(channel, Context.ConnectionId, identity);
    }

    /// <summary>Remove one channel membership from the registry and buffer a leave event (best-effort).</summary>
    private async Task RemovePresenceAsync(string channel)
    {
        try
        {
            await _presence.RemoveAsync(channel, Context.ConnectionId, Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Presence remove failed for connection {ConnectionId} on channel '{Channel}'; continuing.",
                Context.ConnectionId, channel);
        }

        _presenceEvents.RecordLeave(channel, Context.ConnectionId);
    }

    /// <summary>
    /// The disconnect sweep: remove the connection from every channel it was present in and
    /// buffer a leave event on each (task #612). Best-effort — never throws from teardown.
    /// </summary>
    private async Task SweepPresenceAsync(string connectionId)
    {
        try
        {
            // CancellationToken.None: the connection is already gone, so this cleanup must
            // run to completion rather than be abandoned by the aborted request token.
            var channels = await _presence.RemoveConnectionAsync(connectionId, CancellationToken.None);
            foreach (var channel in channels)
            {
                _presenceEvents.RecordLeave(channel, connectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Presence disconnect sweep failed for connection {ConnectionId}; continuing.", connectionId);
        }
    }

    /// <summary>True for channel names in the authenticated <c>ops:*</c> namespace.</summary>
    public static bool IsOpsChannel(string channel) =>
        channel.StartsWith(OpsChannelPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Authorize a join and return the identity to record with the membership: null for an
    /// <c>ops:*</c> or public channel, or the owner-supplied identity for a delegated-auth
    /// (private) channel (task #611). Throws <see cref="HubException"/> on any refusal.
    /// </summary>
    private async Task<string?> AuthorizeChannelAsync(string channel, string? credential)
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

            return null;
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
            return null;
        }

        return await AuthorizeDelegatedAsync(owner, channel, credential);
    }

    /// <summary>
    /// Delegated (private-channel) authorization (task #594): honour a decision already
    /// cached for this connection, else ask the owning service's auth callback and cache
    /// the result — an allow for the connection's lifetime, a deny only briefly. A deny
    /// (whether cached or fresh) throws a generic <see cref="HubException"/> that leaks
    /// nothing about why.
    /// </summary>
    private async Task<string?> AuthorizeDelegatedAsync(ChannelOwner owner, string channel, string? credential)
    {
        var connectionId = Context.ConnectionId;

        // A cached decision short-circuits: an allow admits any credential; a deny only
        // short-circuits a retry of the SAME credential, so a retry with a different,
        // now-valid credential still reaches the owning service (task #608 finding 1).
        if (_decisions.TryGet(connectionId, channel, credential) is { } cached)
        {
            if (cached.Allowed)
            {
                return cached.Identity;
            }

            throw new HubException(AuthDeniedMessage);
        }

        // Rate floor (review finding): denies are keyed per-credential, so a loop over
        // VARYING credentials always misses the deny cache — without this cap it would
        // reach the owner's auth endpoint once per round-trip. Over budget → deny
        // without a callback and without caching (the window itself is the block).
        if (!_decisions.TryRecordAuthAttempt(connectionId, channel))
        {
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
            // The identity (may be null) rides with the cached allow and is recorded on the
            // membership so SendToChannel can stamp forwards with who sent them (task #611).
            _decisions.StoreAllow(connectionId, channel, decision.Identity);
            return decision.Identity;
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
