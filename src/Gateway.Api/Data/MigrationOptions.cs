namespace Gateway.Api.Data;

/// <summary>
/// Retry/backoff policy for applying migrations at boot (tech-spec §6: the box
/// may come up before the database is reachable). Bound to the <c>Migration</c>
/// configuration section. Defaults give a ~2 minute window of exponential
/// backoff; if the database is still unreachable when the window closes the
/// process fails fast (see <see cref="DatabaseMigrationHostedService"/>) so
/// systemd's <c>Restart=always</c> takes over rather than the gateway limping
/// along with a configured-but-unreachable database.
/// </summary>
public sealed class MigrationOptions
{
    /// <summary>Total time to keep retrying before giving up and exiting non-zero.</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Delay before the second attempt; grows by <see cref="BackoffFactor"/> each time.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling on the per-retry backoff.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Exponential growth factor applied to the backoff after each failed attempt.</summary>
    public double BackoffFactor { get; set; } = 2.0;
}
