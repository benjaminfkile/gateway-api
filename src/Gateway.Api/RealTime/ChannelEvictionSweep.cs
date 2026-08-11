using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// The mid-connection channel-eviction sweep (task #613) — closing the phase-2 honest
/// limitation that an admitted PRIVATE-channel member kept receiving events for the
/// connection's lifetime even after its delegated-auth allow lapsed, because nothing removed
/// its SignalR group membership.
/// <para>
/// One pass, driven ~1 min by <see cref="ChannelEvictionService"/>, over this instance's own
/// memberships (<see cref="IPresenceRegistry.LocalMembershipsAsync"/> — eviction touches
/// instance-local state, so each instance evicts only its own connections). Per channel:
/// </para>
/// <list type="bullet">
/// <item><b>ops:*</b> — never evicted here; those channels are Cognito-gated, not
/// delegated-auth, and the token-expiry close already cuts them off.</item>
/// <item><b>owning service deleted</b> (prefix resolves to no owner) — evict <i>every</i>
/// member, reason <see cref="ServiceRemovedReason"/>, so a removed service's channels do not
/// linger until each subscriber happens to disconnect.</item>
/// <item><b>public channel of a live service</b> (owner, no <c>realtime_auth_path</c>) — never
/// evicted: there is no auth to expire.</item>
/// <item><b>private channel</b> (owner has <c>realtime_auth_path</c>) — evict any member whose
/// cached allow has lapsed (<see cref="ChannelAuthDecisionCache.HasValidAllow"/> is false),
/// reason <see cref="AuthExpiredReason"/>.</item>
/// </list>
/// Credentials are never stored, so an expired member cannot be re-authorized in the
/// background; instead the sweep removes it from the group and sends that ONE connection a
/// <c>channelEvicted</c> envelope so a well-behaved client re-joins with a fresh credential
/// (the normal <c>JoinPrivateChannel</c> path readmits it). Best-effort throughout: a per-step
/// failure is logged and never aborts the sweep.
/// </summary>
public sealed class ChannelEvictionSweep
{
    /// <summary>The envelope <c>event</c> name a targeted eviction notice carries.</summary>
    public const string EvictedEvent = "channelEvicted";

    /// <summary>Eviction reason: the member's delegated-auth allow lapsed (needs a fresh credential).</summary>
    public const string AuthExpiredReason = "auth_expired";

    /// <summary>Eviction reason: the channel's owning service was removed from the manifest.</summary>
    public const string ServiceRemovedReason = "service_removed";

    private readonly IPresenceRegistry _presence;
    private readonly IChannelOwnershipResolver _ownership;
    private readonly ChannelAuthDecisionCache _decisions;
    private readonly IHubContext<GatewayHub> _hub;
    private readonly HubChannelMembership _membership;
    private readonly PresenceEventCoalescer _presenceEvents;
    private readonly ILogger<ChannelEvictionSweep> _logger;

    public ChannelEvictionSweep(
        IPresenceRegistry presence,
        IChannelOwnershipResolver ownership,
        ChannelAuthDecisionCache decisions,
        IHubContext<GatewayHub> hub,
        HubChannelMembership membership,
        PresenceEventCoalescer presenceEvents,
        ILogger<ChannelEvictionSweep> logger)
    {
        _presence = presence;
        _ownership = ownership;
        _decisions = decisions;
        _hub = hub;
        _membership = membership;
        _presenceEvents = presenceEvents;
        _logger = logger;
    }

    /// <summary>Run one eviction pass over this instance's memberships. Never throws.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // Enumerate the AUTHORITATIVE in-process membership registry, not the best-effort
        // presence view (review finding): a presence write that failed at join time —
        // e.g. a transient Redis blip — must not exempt that member from eviction for the
        // connection's lifetime. Presence is advisory; HubChannelMembership is truth.
        var memberships = _membership.Snapshot();
        if (memberships.Count == 0)
        {
            return;
        }

        // Group by channel so ownership is resolved once per channel, not once per member
        // (the resolver is snapshot-cached, but per-channel keeps the auth-expiry decision
        // in one place and avoids resolving a deleted/public channel repeatedly).
        foreach (var group in memberships.GroupBy(m => m.Channel, StringComparer.Ordinal))
        {
            var channel = group.Key;

            // ops:* is authenticated at the connection level (Cognito), not delegated-auth —
            // there is no allow to expire, so it is never swept.
            if (GatewayHub.IsOpsChannel(channel))
            {
                continue;
            }

            ChannelOwner? owner;
            try
            {
                owner = await _ownership.ResolveAsync(GatewayHub.PrefixOf(channel), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A snapshot fail-closed (store unreachable past the max-stale bound) must not
                // mass-evict on a transient blip: skip this channel and retry next sweep.
                _logger.LogWarning(
                    ex, "Eviction sweep could not resolve owner for channel '{Channel}'; skipping.", channel);
                continue;
            }

            string reason;
            if (owner is null)
            {
                // The owning service was deleted (or never existed — but an unowned channel is
                // unjoinable, so a present member means the service was removed after joining).
                // Evict every member of the orphaned channel.
                reason = ServiceRemovedReason;
            }
            else if (string.IsNullOrEmpty(owner.AuthPath))
            {
                // Public channel of a live service: no auth to expire, never evicted.
                continue;
            }
            else
            {
                reason = AuthExpiredReason;
            }

            foreach (var member in group)
            {
                // Private channel: keep members that still hold a live allow. For a removed
                // service every member is evicted (reason already fixed above).
                if (reason == AuthExpiredReason && _decisions.HasValidAllow(member.ConnectionId, channel))
                {
                    continue;
                }

                await EvictAsync(channel, member.ConnectionId, reason, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Evict one connection from one channel: drop its SignalR group membership so no further
    /// broadcast reaches it, clear the presence/membership side-registries, and send that
    /// connection alone a <c>channelEvicted</c> envelope. Order matters — the group removal
    /// precedes the notice, and the notice is targeted at the connection (not the group) so it
    /// still arrives after the removal. Each step is independently guarded.
    /// </summary>
    private async Task EvictAsync(string channel, string connectionId, string reason, CancellationToken ct)
    {
        try
        {
            await _hub.Groups.RemoveFromGroupAsync(connectionId, channel, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ABORT the eviction, keeping the membership row intact (review finding): the
            // group removal is the step that actually stops delivery. Deleting the
            // membership/presence state after a FAILED removal would erase the only thing
            // that makes the next sweep retry — turning a transient hub error into a
            // permanent grant of delivery to a member with a lapsed allow.
            _logger.LogWarning(
                ex, "Failed to remove connection {ConnectionId} from group '{Channel}' during eviction; "
                + "membership kept so the next sweep retries.",
                connectionId, channel);
            return;
        }

        try
        {
            await _presence.RemoveAsync(channel, connectionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Failed to remove presence row for {ConnectionId} on '{Channel}' during eviction.",
                connectionId, channel);
        }

        // Side-registries + a coalesced presence leave, mirroring the disconnect sweep so an
        // evicted member can no longer SendToChannel and presence-enabled channels see it go.
        _membership.Leave(connectionId, channel);
        _presenceEvents.RecordLeave(channel, connectionId);

        try
        {
            await _hub.Clients.Client(connectionId).SendAsync(
                IChannelEventPublisher.ChannelEventMethod,
                new { channel, @event = EvictedEvent, data = new { channel, reason } },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Failed to notify connection {ConnectionId} of eviction from '{Channel}'.",
                connectionId, channel);
        }

        _logger.LogInformation(
            "Evicted connection {ConnectionId} from channel '{Channel}' ({Reason}).",
            connectionId, channel, reason);
    }
}
