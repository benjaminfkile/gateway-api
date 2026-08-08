using Gateway.Api.Reconcile;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="ISecretStore"/> for tests — no AWS. Returns the raw
/// <c>SecretString</c> for a configured ref, throws <see cref="SecretResolutionException"/>
/// for an unknown one (mirroring a missing secret), and counts fetches so cache
/// behaviour can be asserted. A <see cref="Handler"/> overrides everything for
/// custom / failure-then-recover scenarios.
/// </summary>
internal sealed class FakeSecretStore : ISecretStore
{
    public int Calls;
    public readonly Dictionary<string, string> Secrets = new(StringComparer.Ordinal);

    /// <summary>When set, fully controls the response (return a value or throw).</summary>
    public Func<string, string>? Handler;

    public Task<string> GetSecretStringAsync(string reference, CancellationToken ct = default)
    {
        Calls++;

        if (Handler is not null)
        {
            return Task.FromResult(Handler(reference));
        }

        if (Secrets.TryGetValue(reference, out var value))
        {
            return Task.FromResult(value);
        }

        throw new SecretResolutionException($"secret '{reference}' was not found in Secrets Manager");
    }
}

/// <summary>A hand-cranked <see cref="TimeProvider"/> so cache-TTL behaviour is deterministic.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => Now;
}
