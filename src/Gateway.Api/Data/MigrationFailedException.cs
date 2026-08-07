namespace Gateway.Api.Data;

/// <summary>
/// Thrown when schema migrations still fail after the whole retry window has
/// elapsed (tech-spec §6). It propagates out of the migration hosted service's
/// <c>StartAsync</c>, aborting host startup so the process exits non-zero and
/// systemd (<c>Restart=always</c>) restarts it — the gateway must never serve
/// traffic with a configured-but-unmigrated database.
/// </summary>
public sealed class MigrationFailedException : Exception
{
    public MigrationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
