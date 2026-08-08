using Gateway.Api.Data;

namespace Gateway.Api.Manifest;

/// <summary>
/// Manifest store backed by an in-memory dictionary. Used by tests and as the
/// local-dev fallback when no database is configured — in that mode it is
/// seedable from the appsettings "Manifest" section so a developer can run the
/// gateway against a static route list without Postgres.
/// </summary>
public sealed class InMemoryManifestStore : IManifestStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ServiceManifest> _byName = new(StringComparer.Ordinal);

    public InMemoryManifestStore()
    {
    }

    /// <summary>Seed the store with an initial set of manifest rows.</summary>
    public InMemoryManifestStore(IEnumerable<ServiceManifest> seed)
    {
        foreach (var m in seed)
        {
            _byName[m.Name] = Clone(m);
        }
    }

    public Task<IReadOnlyList<ServiceManifest>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ServiceManifest> all = _byName.Values.Select(Clone).ToList();
            return Task.FromResult(all);
        }
    }

    public Task<ServiceManifest?> GetAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_byName.TryGetValue(name, out var m) ? Clone(m) : null);
        }
    }

    public Task UpsertAsync(ServiceManifest manifest, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _byName[manifest.Name] = Clone(manifest);
        }

        return Task.CompletedTask;
    }

    public Task SetDesiredStatusAsync(string name, string desiredStatus, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byName.TryGetValue(name, out var m))
            {
                throw new KeyNotFoundException($"No manifest entry for service '{name}'.");
            }

            m.DesiredStatus = desiredStatus;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byName.Remove(name))
            {
                throw new KeyNotFoundException($"No manifest entry for service '{name}'.");
            }
        }

        return Task.CompletedTask;
    }

    // Copies are handed out (and stored) so callers can never mutate the store's
    // state by holding on to a reference.
    private static ServiceManifest Clone(ServiceManifest m) => new()
    {
        Name = m.Name,
        Image = m.Image,
        Tag = m.Tag,
        Digest = m.Digest,
        Port = m.Port,
        DesiredStatus = m.DesiredStatus,
        EnvSecretRef = m.EnvSecretRef,
        IncludeInHealth = m.IncludeInHealth,
        UpdatedBy = m.UpdatedBy,
        UpdatedAt = m.UpdatedAt,
    };
}
