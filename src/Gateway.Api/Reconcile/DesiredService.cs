namespace Gateway.Api.Reconcile;

/// <summary>
/// A manifest entry projected into exactly what the reconciler needs to converge
/// one service: its desired lifecycle state and the identity (digest + env hash)
/// that determines whether a running container is already correct. Keeping this
/// separate from the persisted <see cref="Data.ServiceManifest"/> lets
/// <see cref="ReconcilePlanner"/> stay a pure, easily-tested function over plain
/// data while the reconciler owns the impure work of resolving env/secrets.
/// </summary>
/// <param name="Name">Canonical service name (the container name at steady state).</param>
/// <param name="Image">Registry image reference.</param>
/// <param name="Tag">Image tag to pull when (re)creating the container.</param>
/// <param name="Digest">Resolved image digest the service should run, or null if not yet resolved.</param>
/// <param name="Port">The port the app listens on inside the container.</param>
/// <param name="DesiredStatus">Desired lifecycle state: <c>running</c> or <c>stopped</c>.</param>
/// <param name="EnvHash">Hash of the service's resolved environment; drift from the running container triggers a replace.</param>
/// <param name="EnvVars">The resolved environment to start the container with.</param>
/// <param name="RestartRequestedAt">
/// When a fleet-wide restart was last requested for this service, or null if never. A
/// running container that started before this stamp is stale and gets a blue-green
/// recreate; one started after it satisfies the request (tech-spec §4.5).
/// </param>
public sealed record DesiredService(
    string Name,
    string Image,
    string Tag,
    string? Digest,
    int Port,
    string DesiredStatus,
    string? EnvHash,
    IReadOnlyDictionary<string, string> EnvVars,
    DateTimeOffset? RestartRequestedAt = null)
{
    /// <summary>Whether the service should be running (case-insensitive on the status text).</summary>
    public bool WantsRunning =>
        string.Equals(DesiredStatus, "running", StringComparison.OrdinalIgnoreCase);
}
