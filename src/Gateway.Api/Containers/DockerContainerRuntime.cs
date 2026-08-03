using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Gateway.Api.Containers;

/// <summary>
/// Production <see cref="IContainerRuntime"/> over the Docker daemon's unix
/// socket (<c>unix:///var/run/docker.sock</c>, tech-spec §6). The gateway is the
/// only privileged process on the box and the sole user of the socket
/// (tech-spec §8).
/// <para>
/// This type only ever activates on Linux with the socket present
/// (<see cref="IsSupported"/>); on any other host — including the build/test
/// environment, which has no Docker daemon — it is never registered, and the
/// reconciler runs against a fake runtime instead.
/// </para>
/// </summary>
public sealed class DockerContainerRuntime : IContainerRuntime, IDisposable
{
    /// <summary>Default Docker unix socket path.</summary>
    public const string DefaultSocketPath = "/var/run/docker.sock";

    private readonly IDockerClient _client;

    public DockerContainerRuntime(IDockerClient client)
    {
        _client = client;
    }

    /// <summary>
    /// True only on Linux with the Docker socket present. Callers guard
    /// registration on this so the gateway boots cleanly where Docker is absent.
    /// </summary>
    public static bool IsSupported =>
        OperatingSystem.IsLinux() && File.Exists(DefaultSocketPath);

    /// <summary>Create a runtime bound to the local Docker unix socket.</summary>
    public static DockerContainerRuntime Create()
    {
        var client = new DockerClientConfiguration(new Uri($"unix://{DefaultSocketPath}"))
            .CreateClient();
        return new DockerContainerRuntime(client);
    }

    public async Task EnsureNetworkAsync(string name, CancellationToken ct = default)
    {
        var existing = await _client.Networks.ListNetworksAsync(
            new NetworksListParameters(), ct);
        if (existing.Any(n => string.Equals(n.Name, name, StringComparison.Ordinal)))
        {
            return;
        }

        await _client.Networks.CreateNetworkAsync(
            new NetworksCreateParameters { Name = name, Driver = "bridge" }, ct);
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListManagedContainersAsync(CancellationToken ct = default)
    {
        var listed = await _client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{ContainerLabels.Managed}=true"] = true,
                    },
                },
            }, ct);

        var result = new List<ContainerInfo>(listed.Count);
        foreach (var c in listed)
        {
            var labels = c.Labels ?? new Dictionary<string, string>();
            labels.TryGetValue(ContainerLabels.Digest, out var digest);
            labels.TryGetValue(ContainerLabels.EnvHash, out var envHash);

            // Prefer the daemon's precise start time; fall back to created time.
            DateTimeOffset? startedAt = null;
            var restarts = 0;
            try
            {
                var inspect = await _client.Containers.InspectContainerAsync(c.ID, ct);
                if (DateTimeOffset.TryParse(
                        inspect.State?.StartedAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed) && parsed.Year > 1)
                {
                    startedAt = parsed;
                }

                restarts = (int)inspect.RestartCount;
            }
            catch (DockerApiException)
            {
                // Container vanished between list and inspect; created time still applies.
            }

            startedAt ??= new DateTimeOffset(c.Created, TimeSpan.Zero);

            result.Add(new ContainerInfo(
                Name: (c.Names?.FirstOrDefault() ?? string.Empty).TrimStart('/'),
                Image: c.Image ?? string.Empty,
                Digest: string.IsNullOrEmpty(digest) ? null : digest,
                State: c.State ?? string.Empty,
                StartedAt: startedAt,
                EnvHash: string.IsNullOrEmpty(envHash) ? null : envHash,
                Restarts: restarts));
        }

        return result;
    }

    public async Task<string> PullImageAsync(string image, string tag, CancellationToken ct = default)
    {
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image, Tag = tag },
            authConfig: null,
            progress: new Progress<JSONMessage>(),
            cancellationToken: ct);

        var inspect = await _client.Images.InspectImageAsync($"{image}:{tag}", ct);

        // Prefer a repo digest (registry-content-addressed) over the local image id.
        var repoDigest = inspect.RepoDigests?
            .Select(rd => rd.Contains('@') ? rd[(rd.IndexOf('@') + 1)..] : rd)
            .FirstOrDefault(d => d.StartsWith("sha256:", StringComparison.Ordinal));

        return repoDigest ?? inspect.ID;
    }

    public async Task StartServiceContainerAsync(ServiceContainerSpec spec, CancellationToken ct = default)
    {
        var imageRef = string.IsNullOrEmpty(spec.Digest)
            ? spec.Image
            : $"{spec.Image}@{spec.Digest}";

        var containerPort = $"{spec.Port}/tcp";
        var hostPort = (spec.SidePort ?? spec.Port).ToString(CultureInfo.InvariantCulture);

        var create = new CreateContainerParameters
        {
            Name = spec.Name,
            Image = imageRef,
            Env = spec.EnvVars.Select(kvp => $"{kvp.Key}={kvp.Value}").ToList(),
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.Service] = spec.Name,
                [ContainerLabels.Digest] = spec.Digest ?? string.Empty,
                [ContainerLabels.EnvHash] = spec.EnvHash ?? string.Empty,
            },
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                [containerPort] = default,
            },
            HostConfig = new HostConfig
            {
                // Survive gateway restarts but stay stopped once we stop them (tech-spec §3).
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                NetworkMode = spec.Network,
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [containerPort] = new List<PortBinding>
                    {
                        new() { HostPort = hostPort },
                    },
                },
                LogConfig = new Docker.DotNet.Models.LogConfig
                {
                    Type = spec.LogConfig.Driver,
                    Config = new Dictionary<string, string>(spec.LogConfig.Options),
                },
            },
        };

        var created = await _client.Containers.CreateContainerAsync(create, ct);
        await _client.Containers.StartContainerAsync(
            created.ID, new ContainerStartParameters(), ct);
    }

    public async Task StopAndRemoveAsync(string name, CancellationToken ct = default)
    {
        try
        {
            await _client.Containers.StopContainerAsync(
                name, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            return;
        }
        catch (DockerApiException)
        {
            // Already stopped or not running; proceed to remove.
        }

        try
        {
            await _client.Containers.RemoveContainerAsync(
                name, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone.
        }
    }

    public Task RenameContainerAsync(string oldName, string newName, CancellationToken ct = default) =>
        _client.Containers.RenameContainerAsync(
            oldName, new ContainerRenameParameters { NewName = newName }, ct);

    public void Dispose() => _client.Dispose();
}
