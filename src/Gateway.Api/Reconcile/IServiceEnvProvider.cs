using Gateway.Api.Data;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Resolves the environment (including secrets) a service container should be
/// started with (tech-spec §8: secrets fetched at (re)create time, never stored
/// in the manifest or logs). Behind an interface so the reconciler can be tested
/// without a real secrets store, and so a future task can plug in a Secrets
/// Manager–backed implementation from <see cref="ServiceManifest.EnvSecretRef"/>.
/// </summary>
public interface IServiceEnvProvider
{
    /// <summary>Resolve the environment variables for the given manifest entry.</summary>
    Task<IReadOnlyDictionary<string, string>> GetEnvAsync(ServiceManifest manifest, CancellationToken ct = default);
}

/// <summary>
/// Default no-op provider: returns an empty environment. Secrets-store
/// integration is a later task; until then containers run with only their image
/// defaults, and env-drift detection still works off this (stable) empty set.
/// </summary>
public sealed class NullServiceEnvProvider : IServiceEnvProvider
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>> GetEnvAsync(ServiceManifest manifest, CancellationToken ct = default) =>
        Task.FromResult(Empty);
}
