using System.Text.Json;
using Gateway.Api.Data;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Resolves a service's container environment from AWS Secrets Manager at container
/// (re)create time (tech-spec §8). The service's <see cref="ServiceManifest.EnvSecretRef"/>
/// (a secret name or full ARN) names a secret whose <c>SecretString</c> is a flat JSON
/// object of string values; that object becomes the container env. When the ref is
/// null/empty the environment is empty — exactly the previous
/// <see cref="NullServiceEnvProvider"/> behaviour.
/// <para>
/// Env is resolved at (re)create time and folded into the env hash
/// (<see cref="EnvHasher"/>), so a rotated secret value changes the hash and rolls the
/// container on the next reconcile loop — rotation propagates via env drift, no
/// explicit restart needed.
/// </para>
/// <para>
/// <b>Laziness:</b> constructing the AWS client requires a region, so the client is
/// resolved through a <c>Func&lt;&gt;</c> and only touched on the first non-empty ref
/// — a region-less dev box and the AWS-less CI runner boot and pass host tests.
/// </para>
/// <para>
/// <b>Caching:</b> resolved env is cached per ref for <see cref="ReconcilerOptions.SecretCacheTtl"/>
/// so a 30s fleet loop does not hit Secrets Manager per service per loop, while a
/// rotated value still lands within ~TTL + one loop. The cache is thread-safe and a
/// failed fetch is never cached. Secret values never appear in logs or error messages.
/// </para>
/// </summary>
public sealed class SecretsManagerEnvProvider : IServiceEnvProvider
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly Func<ISecretStore> _store;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

    private readonly record struct CacheEntry(IReadOnlyDictionary<string, string> Env, DateTimeOffset FetchedAt);

    public SecretsManagerEnvProvider(Func<ISecretStore> store, TimeSpan ttl, TimeProvider? time = null)
    {
        _store = store;
        _ttl = ttl;
        _time = time ?? TimeProvider.System;
    }

    public SecretsManagerEnvProvider(ISecretStore store, TimeSpan ttl, TimeProvider? time = null)
        : this(() => store, ttl, time)
    {
    }

    public async Task<IReadOnlyDictionary<string, string>> GetEnvAsync(
        ServiceManifest manifest, CancellationToken ct = default)
    {
        var reference = manifest.EnvSecretRef;
        if (string.IsNullOrWhiteSpace(reference))
        {
            // No secret configured → empty env (the exact prior no-op behaviour). The
            // AWS client is never touched, so this stays region-independent.
            return Empty;
        }

        reference = reference.Trim();

        if (TryGetFresh(reference, out var fresh))
        {
            return fresh;
        }

        // Serialise refreshes per ref so a burst of services sharing a ref (or the
        // same service across a fast loop) fetches at most once. A failed fetch throws
        // before anything is cached, so failures are never memoised (requirement #3).
        var refreshLock = RefreshLockFor(reference);
        await refreshLock.WaitAsync(ct);
        try
        {
            if (TryGetFresh(reference, out fresh))
            {
                return fresh;
            }

            var secretString = await _store().GetSecretStringAsync(reference, ct);
            var env = Parse(reference, secretString);

            lock (_gate)
            {
                _cache[reference] = new CacheEntry(env, _time.GetUtcNow());
            }

            return env;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private bool TryGetFresh(string reference, out IReadOnlyDictionary<string, string> env)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(reference, out var entry)
                && _time.GetUtcNow() - entry.FetchedAt < _ttl)
            {
                env = entry.Env;
                return true;
            }
        }

        env = Empty;
        return false;
    }

    private SemaphoreSlim RefreshLockFor(string reference)
    {
        lock (_gate)
        {
            if (!_refreshLocks.TryGetValue(reference, out var refreshLock))
            {
                refreshLock = new SemaphoreSlim(1, 1);
                _refreshLocks[reference] = refreshLock;
            }

            return refreshLock;
        }
    }

    /// <summary>
    /// Parse a <c>SecretString</c> as a flat JSON object of string values. Any other
    /// shape — invalid JSON, a non-object root, or a nested object/array/number/bool
    /// value — is a <see cref="SecretResolutionException"/> naming the ref (and, for a
    /// bad member, its key) but <b>never</b> echoing a value.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Parse(string reference, string secretString)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(secretString);
        }
        catch (JsonException ex)
        {
            throw new SecretResolutionException(
                $"secret '{reference}' is not valid JSON; expected a flat object of string values", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new SecretResolutionException(
                    $"secret '{reference}' is not a flat JSON object of string values");
            }

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (member.Value.ValueKind != JsonValueKind.String)
                {
                    throw new SecretResolutionException(
                        $"secret '{reference}' has a non-string value for key '{member.Name}'; "
                        + "nested objects/arrays and non-string values are not supported");
                }

                env[member.Name] = member.Value.GetString()!;
            }

            return env;
        }
    }
}
