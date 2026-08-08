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
                StartedAt: DateTimeOffset.UnixEpoch,
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
}
