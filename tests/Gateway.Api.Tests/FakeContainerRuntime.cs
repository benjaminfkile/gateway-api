using System.Collections.Concurrent;
using Gateway.Api.Containers;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IContainerRuntime"/> for tests. Models just enough Docker
/// behaviour to exercise the reconciler and blue-green flow deterministically —
/// no daemon, no network. Every mutating call is recorded in <see cref="Operations"/>
/// so tests can assert ordering (e.g. that the old container is only removed after
/// the green candidate is healthy).
/// </summary>
public sealed class FakeContainerRuntime : IContainerRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ContainerInfo> _containers = new(StringComparer.Ordinal);

    /// <summary>Networks that have been ensured.</summary>
    public HashSet<string> Networks { get; } = new(StringComparer.Ordinal);

    /// <summary>Ordered log of mutating operations, for asserting sequence.</summary>
    public ConcurrentQueue<string> Operations { get; } = new();

    /// <summary>Specs passed to <see cref="StartServiceContainerAsync"/>, in order.</summary>
    public ConcurrentQueue<ServiceContainerSpec> StartedSpecs { get; } = new();

    /// <summary>Digest returned by <see cref="PullImageAsync"/>; keyed by "image:tag", else the default.</summary>
    public Func<string, string, string> PullDigest { get; set; } = (image, tag) => $"sha256:{image}-{tag}";

    // Models Docker's dynamic host-port assignment: each started container gets a
    // fresh, unique ephemeral host port (Docker's default range starts at 49152),
    // so no two live containers ever contend for a port.
    private int _nextHostPort = 49152;

    /// <summary>The next ephemeral host port the fake will assign on the next start.</summary>
    public int NextHostPort => _nextHostPort;

    /// <summary>
    /// The <see cref="ContainerInfo.StartedAt"/> stamped on containers created by
    /// <see cref="StartServiceContainerAsync"/> — models Docker recording each
    /// container's start time. Defaults to the Unix epoch (so a plain start matches a
    /// container seeded at the epoch); restart-drift tests set a later instant so a
    /// recreated container is newer than the manifest's <c>restart_requested_at</c>.
    /// </summary>
    public DateTimeOffset NewContainerStartedAt { get; set; } = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Digests that are "missing" locally: a start referencing one throws
    /// <see cref="ContainerImageNotFoundException"/>, modelling Docker's
    /// "No such image ...@&lt;digest&gt;" for a stale pinned digest.
    /// </summary>
    public HashSet<string> MissingDigests { get; } = new(StringComparer.Ordinal);

    /// <summary>Seed a pre-existing container (e.g. an already-running old service).</summary>
    public void Seed(ContainerInfo container)
    {
        lock (_gate)
        {
            _containers[container.Name] = container;
        }
    }

    /// <summary>Whether a container with the given name currently exists.</summary>
    public bool Exists(string name)
    {
        lock (_gate)
        {
            return _containers.ContainsKey(name);
        }
    }

    /// <summary>Current snapshot of a container by name, or null.</summary>
    public ContainerInfo? Get(string name)
    {
        lock (_gate)
        {
            return _containers.TryGetValue(name, out var c) ? c : null;
        }
    }

    public Task EnsureNetworkAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Networks.Add(name);
        }

        Operations.Enqueue($"EnsureNetwork:{name}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContainerInfo>> ListManagedContainersAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ContainerInfo> list = _containers.Values.ToList();
            return Task.FromResult(list);
        }
    }

    public Task<string> PullImageAsync(string image, string tag, CancellationToken ct = default)
    {
        Operations.Enqueue($"Pull:{image}:{tag}");
        return Task.FromResult(PullDigest(image, tag));
    }

    public Task<int> StartServiceContainerAsync(ServiceContainerSpec spec, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(spec.Digest) && MissingDigests.Contains(spec.Digest))
        {
            var imageRef = $"{spec.Image}@{spec.Digest}";
            Operations.Enqueue($"StartFailedImageNotFound:{spec.Name}");
            throw new ContainerImageNotFoundException($"No such image {imageRef}", imageRef);
        }

        int hostPort;
        lock (_gate)
        {
            // Docker assigns a unique ephemeral host port at start time; the manifest
            // port is only the container-internal port and never a host binding.
            hostPort = _nextHostPort++;
            _containers[spec.Name] = new ContainerInfo(
                Name: spec.Name,
                Image: spec.Image,
                Digest: spec.Digest,
                State: "running",
                StartedAt: NewContainerStartedAt,
                EnvHash: spec.EnvHash,
                HostPort: hostPort);
        }

        StartedSpecs.Enqueue(spec);
        Operations.Enqueue($"Start:{spec.Name}");
        return Task.FromResult(hostPort);
    }

    public Task StopAndRemoveAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _containers.Remove(name);
        }

        Operations.Enqueue($"StopRemove:{name}");
        return Task.CompletedTask;
    }

    public Task RenameContainerAsync(string oldName, string newName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_containers.Remove(oldName, out var c))
            {
                _containers[newName] = c with { Name = newName };
            }
        }

        Operations.Enqueue($"Rename:{oldName}->{newName}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A local image on the fake daemon. Mirrors the fields
    /// <see cref="PruneImagesAsync"/> reads from Docker's image listing.
    /// </summary>
    public sealed record FakeImage(
        string Id,
        IReadOnlyList<string> RepoTags,
        IReadOnlyList<string> RepoDigests,
        DateTime Created,
        long Size);

    /// <summary>
    /// Arguments passed to a <see cref="PruneImagesAsync"/> call, in order.
    /// </summary>
    public sealed record PruneCall(
        IReadOnlyCollection<string> KeepDigests,
        TimeSpan MinimumAge,
        int KeepPerRepository);

    private readonly List<FakeImage> _images = new();

    /// <summary>Log of every <see cref="PruneImagesAsync"/> call, in order.</summary>
    public ConcurrentQueue<PruneCall> PruneCalls { get; } = new();

    /// <summary>
    /// Clock used to compute the age cutoff during <see cref="PruneImagesAsync"/>.
    /// Overridable so tests can pin "now" without waiting real time.
    /// </summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// If set, the next <see cref="PruneImagesAsync"/> call throws this and clears
    /// the field — models a transient daemon failure so tests can prove a prune
    /// failure never propagates out of the reconcile loop.
    /// </summary>
    public Exception? NextPruneThrow { get; set; }

    /// <summary>Snapshot of local images currently on the fake daemon.</summary>
    public IReadOnlyList<FakeImage> Images
    {
        get
        {
            lock (_gate)
            {
                return _images.ToArray();
            }
        }
    }

    /// <summary>Replace the fake's local image list.</summary>
    public void SetImages(IEnumerable<FakeImage> images)
    {
        lock (_gate)
        {
            _images.Clear();
            _images.AddRange(images);
        }
    }

    /// <summary>Add a single image to the fake's local list.</summary>
    public void AddImage(FakeImage image)
    {
        lock (_gate)
        {
            _images.Add(image);
        }
    }

    /// <summary>
    /// Digests (by fake image ID) that fail deletion with a simulated 409
    /// Conflict — models the daemon refusing to remove an image still in use.
    /// </summary>
    public HashSet<string> InUseImageIds { get; } = new(StringComparer.Ordinal);

    public Task<ImagePruneResult> PruneImagesAsync(
        IReadOnlyCollection<string> keepDigests,
        TimeSpan minimumAge,
        int keepPerRepository,
        CancellationToken ct = default)
    {
        PruneCalls.Enqueue(new PruneCall(keepDigests.ToArray(), minimumAge, keepPerRepository));

        if (NextPruneThrow is { } toThrow)
        {
            NextPruneThrow = null;
            throw toThrow;
        }

        if (minimumAge < TimeSpan.Zero)
        {
            minimumAge = TimeSpan.Zero;
        }

        if (keepPerRepository < 0)
        {
            keepPerRepository = 0;
        }

        var keep = new HashSet<string>(keepDigests, StringComparer.Ordinal);

        List<ImagePruneSelector.Candidate> candidates;
        FakeImage[] snapshot;
        lock (_gate)
        {
            snapshot = _images.ToArray();
        }

        var projected = snapshot
            .Select(i => new ImagePruneSelector.Candidate(i.Id, i.RepoTags, i.RepoDigests, i.Created, i.Size))
            .ToArray();
        candidates = ImagePruneSelector.SelectCandidates(projected, keep, UtcNow() - minimumAge, keepPerRepository);

        var deleted = 0;
        long bytes = 0L;
        foreach (var c in candidates)
        {
            if (InUseImageIds.Contains(c.Id))
            {
                continue;
            }

            lock (_gate)
            {
                var index = _images.FindIndex(i => string.Equals(i.Id, c.Id, StringComparison.Ordinal));
                if (index < 0)
                {
                    continue;
                }

                _images.RemoveAt(index);
            }

            deleted++;
            bytes += c.Size;
        }

        Operations.Enqueue($"PruneImages:{deleted}");
        return Task.FromResult(deleted == 0 && bytes == 0L
            ? ImagePruneResult.None
            : new ImagePruneResult(deleted, bytes));
    }
}
