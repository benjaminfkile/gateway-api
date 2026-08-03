namespace Gateway.Api.Containers;

/// <summary>
/// Abstraction over the local container engine (Docker) that the node reconciler
/// drives (tech-spec §4.3). Every interaction with the Docker daemon goes through
/// this seam so the reconciler can be unit-tested with a
/// <c>FakeContainerRuntime</c> — the build/test environment has no Docker daemon.
/// The production implementation is <see cref="DockerContainerRuntime"/> over the
/// unix socket.
/// </summary>
public interface IContainerRuntime
{
    /// <summary>
    /// Ensure the internal Docker network exists, creating it if absent. Idempotent.
    /// </summary>
    Task EnsureNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// List the containers this gateway manages (marked with the
    /// <c>gateway.managed</c> label). Containers the gateway did not create are
    /// never returned and never touched.
    /// </summary>
    Task<IReadOnlyList<ContainerInfo>> ListManagedContainersAsync(CancellationToken ct = default);

    /// <summary>
    /// Pull <paramref name="image"/>:<paramref name="tag"/> from the registry and
    /// return the resolved sha256 digest.
    /// </summary>
    Task<string> PullImageAsync(string image, string tag, CancellationToken ct = default);

    /// <summary>
    /// Create and start a service container from <paramref name="spec"/>. The
    /// container is started with <c>--restart unless-stopped</c> and the
    /// configured log driver, joins the given network, and is labelled as managed.
    /// </summary>
    Task StartServiceContainerAsync(ServiceContainerSpec spec, CancellationToken ct = default);

    /// <summary>Stop (if running) and remove the named container. Idempotent.</summary>
    Task StopAndRemoveAsync(string name, CancellationToken ct = default);

    /// <summary>Rename a container, e.g. promote <c>{name}-green</c> to the canonical name.</summary>
    Task RenameContainerAsync(string oldName, string newName, CancellationToken ct = default);
}
