namespace Gateway.Api.Data;

/// <summary>
/// A one-shot gate signalling "schema migrations have been applied" (tech-spec
/// §1 requirement for this task): anything that reads the schema — the reconciler
/// loop, the instance-status heartbeat — awaits <see cref="WaitAsync"/> so it can
/// never race ahead of <see cref="DatabaseMigrationHostedService"/> and query a
/// table that does not exist yet.
/// <para>
/// When no database is configured (the in-memory manifest path) the gate is
/// created <see cref="AlreadyReady"/>, so DB-less boots proceed immediately with
/// zero database traffic. When a database <i>is</i> configured the gate starts
/// pending and is opened by the migration hosted service once migrations succeed.
/// </para>
/// </summary>
public sealed class MigrationReadinessGate
{
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a gate that opens only when <see cref="MarkReady"/> is called.</summary>
    public MigrationReadinessGate()
    {
    }

    /// <summary>A gate that is already open — used where no migration is required.</summary>
    public static MigrationReadinessGate AlreadyReady()
    {
        var gate = new MigrationReadinessGate();
        gate.MarkReady();
        return gate;
    }

    /// <summary>True once migrations have completed and the gate is open.</summary>
    public bool IsReady => _completion.Task.IsCompletedSuccessfully;

    /// <summary>Open the gate, releasing everything awaiting <see cref="WaitAsync"/>.</summary>
    public void MarkReady() => _completion.TrySetResult();

    /// <summary>
    /// Wait until the gate opens. Completes immediately if it is already open;
    /// otherwise blocks until <see cref="MarkReady"/> is called or <paramref name="ct"/>
    /// is cancelled (host shutdown).
    /// </summary>
    public Task WaitAsync(CancellationToken ct = default) => _completion.Task.WaitAsync(ct);
}
