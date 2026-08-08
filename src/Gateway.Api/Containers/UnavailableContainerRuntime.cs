namespace Gateway.Api.Containers;

/// <summary>
/// Placeholder <see cref="IContainerRuntime"/> registered when no Docker daemon
/// is present (non-Linux hosts, or the build/test environment). It is never
/// invoked in normal operation because the reconciler is disabled by default and
/// only runs where Docker exists; if something does call it, throwing makes the
/// misconfiguration (reconciler enabled without Docker) obvious rather than
/// silently no-op'ing.
/// </summary>
public sealed class UnavailableContainerRuntime : IContainerRuntime
{
    private static InvalidOperationException Unavailable() =>
        new("No container runtime is available on this host (Docker socket not present). " +
            "Enable the reconciler only where Docker is running.");

    public Task EnsureNetworkAsync(string name, CancellationToken ct = default) => throw Unavailable();

    public Task<IReadOnlyList<ContainerInfo>> ListManagedContainersAsync(CancellationToken ct = default) =>
        throw Unavailable();

    public Task<string> PullImageAsync(string image, string tag, CancellationToken ct = default) =>
        throw Unavailable();

    public Task<int> StartServiceContainerAsync(ServiceContainerSpec spec, CancellationToken ct = default) =>
        throw Unavailable();

    public Task StopAndRemoveAsync(string name, CancellationToken ct = default) => throw Unavailable();

    public Task RenameContainerAsync(string oldName, string newName, CancellationToken ct = default) =>
        throw Unavailable();
}
