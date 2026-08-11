namespace Gateway.Api.RealTime;

/// <summary>
/// A projection of the shared <see cref="ManifestSnapshotCache"/> snapshot, memoized
/// against the snapshot reference so it is rebuilt only when the snapshot actually
/// changes — not on every call. Extracted because the hub CORS origin set and the
/// channel ownership map each carried a byte-for-byte copy of this double-checked
/// scaffolding (review finding): the idiom's concurrency semantics live here once.
/// <para>
/// Semantics: the projection function is pure over the snapshot and cheap relative to
/// the TTL, so concurrent first-projections of a new snapshot may race — last writer
/// wins, both produce equivalent values, and no lock is held while projecting.
/// </para>
/// </summary>
public sealed class SnapshotProjection<T> where T : class
{
    private readonly ManifestSnapshotCache _snapshots;
    private readonly Func<ManifestSnapshotCache.Snapshot, T> _project;
    private readonly object _gate = new();

    private ManifestSnapshotCache.Snapshot? _projectedFrom;
    private T? _projected;

    public SnapshotProjection(ManifestSnapshotCache snapshots, Func<ManifestSnapshotCache.Snapshot, T> project)
    {
        _snapshots = snapshots;
        _project = project;
    }

    /// <summary>
    /// The projection of the current snapshot, recomputed only when the underlying
    /// snapshot object changes. Propagates the cache's fail-closed exception when no
    /// servable snapshot exists.
    /// </summary>
    public async Task<T> GetAsync(CancellationToken ct = default)
    {
        var snapshot = await _snapshots.GetAsync(ct);

        lock (_gate)
        {
            if (ReferenceEquals(snapshot, _projectedFrom) && _projected is not null)
            {
                return _projected;
            }
        }

        var projected = _project(snapshot);

        lock (_gate)
        {
            _projectedFrom = snapshot;
            _projected = projected;
        }

        return projected;
    }
}
