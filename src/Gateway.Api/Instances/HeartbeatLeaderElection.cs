using Gateway.Api.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Instances;

/// <summary>
/// Heartbeat-derived fleet leader election (tech-spec §4.3). There is <b>no lock,
/// no lease, and no session state</b>: the leader is simply the live instance with
/// the lowest <c>instance_id</c> (ordinal string compare) among the
/// <c>instance_status</c> rows whose <see cref="InstanceStatus.HeartbeatAt"/> is
/// within <see cref="_staleThreshold"/>. Because a hard-killed instance stops
/// heartbeating, its row goes stale and drops out of the candidate set on its own —
/// leadership moves to the next-lowest live id within one threshold of the death,
/// with none of the zombie-connection pathology of a Postgres advisory lock.
/// <para>
/// The leader-only duties (stale <c>instance_status</c> pruning, deploy completion
/// marking) are <b>idempotent</b>, so strict mutual exclusion is unnecessary: a
/// brief dual-leader overlap during a transition is harmless by design. Liveness
/// (someone is always leading a healthy fleet) matters more than exclusivity.
/// </para>
/// <para>
/// Each call <b>upserts this instance's own heartbeat first, then evaluates
/// leadership from a fresh read</b> — a booting instance must be able to see itself
/// and take leadership when it is the lowest live id. The reconciler republishes
/// this instance's full inventory and the authoritative <c>is_leader</c> flag on the
/// same row immediately afterwards. Reads/writes go through
/// <see cref="IInstanceStatusStore"/> (resolved per call from a scope, since the EF
/// store is scoped) so the algorithm is unit-tested against a fake — the build box
/// has no Postgres. <see cref="InMemoryLeaderElection"/> still stands in for no-DB
/// single-node mode.
/// </para>
/// </summary>
public sealed class HeartbeatLeaderElection : ILeaderElection
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InstanceMetadataProvider _metadata;
    private readonly TimeSpan _staleThreshold;
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Production constructor. <paramref name="scopeFactory"/> yields a scoped
    /// <see cref="IInstanceStatusStore"/> per evaluation; <paramref name="staleThreshold"/>
    /// is <see cref="Reconcile.ReconcilerOptions.InstanceStaleThreshold"/> (the same
    /// 90s window that marks an <c>instance_status</c> row departed, tech-spec §4.4).
    /// </summary>
    public HeartbeatLeaderElection(
        IServiceScopeFactory scopeFactory,
        InstanceMetadataProvider metadata,
        TimeSpan staleThreshold,
        ILogger<HeartbeatLeaderElection>? logger = null,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _metadata = metadata;
        _staleThreshold = staleThreshold;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct = default)
    {
        var identity = await _metadata.GetAsync(ct);
        var myId = identity.InstanceId;
        var now = _clock.GetUtcNow();

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IInstanceStatusStore>();

        // Upsert our own heartbeat FIRST so the fresh read below always includes this
        // instance (a booting box must see itself). This is a lightweight heartbeat
        // touch — the reconciler overwrites the same row with the full service
        // inventory and the authoritative is_leader immediately after this returns.
        await store.UpsertAsync(
            new InstanceStatus
            {
                InstanceId = myId,
                PrivateIp = identity.PrivateIp,
                PublicIp = identity.PublicIp,
                HeartbeatAt = now,
            },
            ct);

        // Leader = lowest instance_id among rows heartbeating within the threshold.
        // Stale rows (a departed or hard-killed instance) are excluded, so leadership
        // moves to the next-lowest live id within one threshold of a leader's death.
        var rows = await store.GetAllAsync(ct);
        string? leaderId = null;
        foreach (var row in rows)
        {
            if (now - row.HeartbeatAt > _staleThreshold)
            {
                continue;
            }

            if (leaderId is null || string.CompareOrdinal(row.InstanceId, leaderId) < 0)
            {
                leaderId = row.InstanceId;
            }
        }

        // leaderId is never null here: our own just-written heartbeat is always a
        // fresh candidate. Guard defensively anyway.
        var isLeader = leaderId is not null && string.Equals(leaderId, myId, StringComparison.Ordinal);

        _logger?.LogDebug(
            "Heartbeat leader election: instance {Me} sees leader {Leader} (isLeader={IsLeader}).",
            myId, leaderId ?? "(none)", isLeader);

        return isLeader;
    }
}
