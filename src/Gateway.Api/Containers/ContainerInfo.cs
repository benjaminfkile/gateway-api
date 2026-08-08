namespace Gateway.Api.Containers;

/// <summary>
/// A snapshot of a gateway-managed container as reported by
/// <see cref="IContainerRuntime.ListManagedContainersAsync"/>. Only containers
/// the gateway created (marked with the <c>gateway.managed</c> label) are
/// returned — the reconciler never touches containers it does not own.
/// </summary>
/// <param name="Name">Container name (canonical service name, or <c>{name}-green</c> during a blue-green swap).</param>
/// <param name="Image">Image reference the container was created from.</param>
/// <param name="Digest">
/// Resolved image digest the container is running, captured as a label at start
/// time so digest drift can be detected without inspecting the registry. Null
/// when unknown (e.g. a container not started by this gateway).
/// </param>
/// <param name="State">Docker container state, e.g. <c>running</c> / <c>exited</c>.</param>
/// <param name="StartedAt">When the container last started, or null when unknown.</param>
/// <param name="EnvHash">
/// Hash of the environment the container was created with, captured as a label so
/// env drift triggers a blue-green replace. Null when unknown.
/// </param>
/// <param name="HostPort">
/// The host-published port the container is <b>actually</b> bound to. Host ports are
/// Docker-assigned (dynamic): the runtime publishes the container-internal port with
/// an unassigned host binding, and Docker picks a unique ephemeral port. The gateway
/// forwards to this real port, not the manifest port (which is only the
/// container-side contract). Null when unknown (e.g. a container not started by this
/// gateway).
/// </param>
/// <param name="Restarts">
/// How many times the container has been restarted by Docker, surfaced in the
/// fleet <c>instance_status</c> heartbeat (tech-spec §4.4). Defaults to 0 when the
/// runtime does not report it.
/// </param>
public sealed record ContainerInfo(
    string Name,
    string Image,
    string? Digest,
    string State,
    DateTimeOffset? StartedAt,
    string? EnvHash,
    int? HostPort = null,
    int Restarts = 0);
