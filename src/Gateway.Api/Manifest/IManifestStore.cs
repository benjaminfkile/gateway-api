using Gateway.Api.Data;

namespace Gateway.Api.Manifest;

/// <summary>
/// Read/write access to the desired-state manifest (tech-spec §4.4). The proxy
/// builds routes from what this returns; the reconciler and Management API
/// mutate it. Two implementations exist: <see cref="EfManifestStore"/> over
/// Postgres (production) and <see cref="InMemoryManifestStore"/> (tests and
/// local dev when no database is configured).
/// </summary>
public interface IManifestStore
{
    /// <summary>All manifest rows, in no particular order.</summary>
    Task<IReadOnlyList<ServiceManifest>> GetAllAsync(CancellationToken ct = default);

    /// <summary>A single manifest row by service name, or null when absent.</summary>
    Task<ServiceManifest?> GetAsync(string name, CancellationToken ct = default);

    /// <summary>Insert or replace the row for <paramref name="manifest"/>.Name.</summary>
    Task UpsertAsync(ServiceManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Set the desired lifecycle state ('running' | 'stopped') for a service.
    /// Throws <see cref="KeyNotFoundException"/> if the service is unknown.
    /// </summary>
    Task SetDesiredStatusAsync(string name, string desiredStatus, CancellationToken ct = default);

    /// <summary>
    /// Remove the manifest row for <paramref name="name"/>. The reconciler treats a
    /// managed container with no manifest entry as an orphan and stops/removes it on
    /// its next loop (tech-spec §4.3), so deletion is purely a manifest-row operation.
    /// Throws <see cref="KeyNotFoundException"/> if the service is unknown.
    /// </summary>
    Task DeleteAsync(string name, CancellationToken ct = default);
}
