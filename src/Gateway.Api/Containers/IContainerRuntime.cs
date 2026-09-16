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
    /// The container-internal port (<see cref="ServiceContainerSpec.Port"/>) is
    /// published with an <b>unassigned</b> host binding so Docker picks a unique
    /// ephemeral host port; the method inspects the started container and returns
    /// the actual assigned host port. The caller records this so the proxy and
    /// health prober can reach the container (tech-spec §4.1, §7).
    /// </summary>
    Task<int> StartServiceContainerAsync(ServiceContainerSpec spec, CancellationToken ct = default);

    /// <summary>Stop (if running) and remove the named container. Idempotent.</summary>
    Task StopAndRemoveAsync(string name, CancellationToken ct = default);

    /// <summary>Rename a container, e.g. promote <c>{name}-green</c> to the canonical name.</summary>
    Task RenameContainerAsync(string oldName, string newName, CancellationToken ct = default);

    /// <summary>
    /// Delete local images the gateway no longer needs so per-deploy layers do
    /// not fill the node's root volume (tech-spec §4.3 Housekeeping). The scan
    /// is deliberately conservative — deleting an image a container still needs
    /// would break a rollback or a restart, so the daemon is left as the final
    /// authority (each delete is issued with <c>Force = false</c>, letting Docker
    /// refuse the removal if any container still references the image).
    /// <para>
    /// An image is a delete candidate only when all of the following hold:
    /// its ID is not in <paramref name="keepDigests"/> and none of its repo
    /// digests is either; its Created timestamp is older than
    /// <paramref name="minimumAge"/>; and it is not among the
    /// <paramref name="keepPerRepository"/> most-recently-created images for
    /// its repository (dangling / <c>&lt;none&gt;</c>-tagged images all share a
    /// single bucket and are subject to the same rule). Candidates are removed
    /// oldest first, one at a time; a per-image failure — including a
    /// <c>409 Conflict</c> from the daemon saying the image is still in use —
    /// is skipped and the run continues with the next candidate.
    /// </para>
    /// </summary>
    /// <param name="keepDigests">
    /// Digests the caller wants preserved. Both image IDs and repo digests
    /// (the <c>sha256:…</c> part after the <c>@</c> in a repo digest) are
    /// checked against this set, because <see cref="PullImageAsync"/> returns
    /// the repo digest while an image's local ID is a different sha.
    /// </param>
    /// <param name="minimumAge">
    /// Images younger than this are always kept, matching the tech-spec's
    /// "prune untagged images older than 48h" grace period. Negative values
    /// are treated as <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <param name="keepPerRepository">
    /// Retain at least this many of the most-recently-created images per
    /// repository so a hot rollback target is always local. Negative values
    /// are treated as 0.
    /// </param>
    Task<ImagePruneResult> PruneImagesAsync(
        IReadOnlyCollection<string> keepDigests,
        TimeSpan minimumAge,
        int keepPerRepository,
        CancellationToken ct = default);
}
