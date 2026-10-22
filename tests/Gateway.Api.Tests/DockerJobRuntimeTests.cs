using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Gateway.Api.Containers;

namespace Gateway.Api.Tests;

/// <summary>
/// <see cref="DockerContainerRuntime"/>'s job-run path against a scripted Docker
/// client (no daemon on the build box): the create parameters carry the job labels
/// only, no exposed or published port, restart policy <c>no</c> and the memory limit;
/// timeout and cancel kill; the tail comes from the logs API; the container is always
/// removed; and the reconciler's listing skips any container with the job label.
/// </summary>
public class DockerJobRuntimeTests
{
    /// <summary>
    /// A <see cref="DispatchProxy"/> that answers interface calls by method name from
    /// <see cref="Handlers"/> and records every call. Unhandled calls throw.
    /// </summary>
    public class ScriptedProxy : DispatchProxy
    {
        public Dictionary<string, Func<object?[], object?>> Handlers { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<(string Method, object?[] Args)> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            Calls.Enqueue((name, args ?? Array.Empty<object?>()));
            if (Handlers.TryGetValue(name, out var handler))
            {
                return handler(args ?? Array.Empty<object?>());
            }

            throw new NotSupportedException($"Unscripted Docker call {name}");
        }
    }

    private sealed class Docker
    {
        public Docker()
        {
            Client = DispatchProxy.Create<IDockerClient, ScriptedProxy>();
            ContainersProxy = DispatchProxy.Create<IContainerOperations, ScriptedProxy>();
            var client = (ScriptedProxy)(object)Client;
            client.Handlers["get_Containers"] = _ => ContainersProxy;
            client.Handlers["Dispose"] = _ => null;

            Containers.Handlers["CreateContainerAsync"] = args =>
            {
                Created = (CreateContainerParameters)args[0]!;
                return Task.FromResult(new CreateContainerResponse { ID = "c0ffee" });
            };
            Containers.Handlers["StartContainerAsync"] = _ => Task.FromResult(true);
            Containers.Handlers["WaitContainerAsync"] = args => Wait((CancellationToken)args[1]!);
            Containers.Handlers["KillContainerAsync"] = _ => Task.CompletedTask;
            Containers.Handlers["RemoveContainerAsync"] = _ => Task.CompletedTask;
            Containers.Handlers["GetContainerLogsAsync"] = _ => Task.FromResult(Logs(LogOutput));
        }

        public IDockerClient Client { get; }
        public IContainerOperations ContainersProxy { get; }
        public ScriptedProxy Containers => (ScriptedProxy)(object)ContainersProxy;
        public CreateContainerParameters? Created { get; private set; }
        public string LogOutput { get; set; } = string.Empty;

        /// <summary>The wait call's behaviour; defaults to exit 0 at once.</summary>
        public Func<CancellationToken, Task<ContainerWaitResponse>> Wait { get; set; } =
            _ => Task.FromResult(new ContainerWaitResponse { StatusCode = 0 });

        public IEnumerable<string> CallNames => Containers.Calls.Select(c => c.Method);

        public DockerContainerRuntime Runtime() => new(Client);
    }

    /// <summary>A multiplexed log stream as Docker frames it for a non-tty container.</summary>
    private static MultiplexedStream Logs(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return new MultiplexedStream(new MemoryStream(frame), true);
    }

    private static JobContainerSpec Spec() => new(
        Name: "job-mirror-42",
        Image: "gateway-job-runner",
        Digest: "sha256:0123",
        EnvVars: new Dictionary<string, string> { ["GATEWAY_JOB_NAME"] = "mirror" },
        Network: "app-net",
        LogConfig: new LogDriverConfig { Driver = "awslogs", Options = { ["awslogs-group"] = "/gateway/jobs/mirror" } },
        MemoryLimitBytes: 1L << 30,
        Labels: new Dictionary<string, string>
        {
            [ContainerLabels.Job] = "mirror",
            [ContainerLabels.JobRun] = "42",
        });

    [Fact]
    public async Task Run_CreatesWithJobLabelsOnly_NoPorts_NoRestart_MemoryLimit_ThenRemoves()
    {
        var docker = new Docker
        {
            LogOutput = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"line {i}")) + "\n",
            Wait = _ => Task.FromResult(new ContainerWaitResponse { StatusCode = 3 }),
        };

        var result = await docker.Runtime().RunJobContainerAsync(Spec(), TimeSpan.FromMinutes(1), CancellationToken.None);

        var created = docker.Created!;
        Assert.Equal("job-mirror-42", created.Name);
        Assert.Equal("gateway-job-runner@sha256:0123", created.Image);
        Assert.Equal(
            new Dictionary<string, string> { [ContainerLabels.Job] = "mirror", [ContainerLabels.JobRun] = "42" },
            created.Labels);
        Assert.True(created.ExposedPorts is null || created.ExposedPorts.Count == 0);
        Assert.True(created.HostConfig.PortBindings is null || created.HostConfig.PortBindings.Count == 0);
        Assert.False(created.HostConfig.PublishAllPorts);
        Assert.Equal(RestartPolicyKind.No, created.HostConfig.RestartPolicy.Name);
        Assert.Equal(1L << 30, created.HostConfig.Memory);
        Assert.Equal("app-net", created.HostConfig.NetworkMode);
        Assert.Equal("awslogs", created.HostConfig.LogConfig.Type);
        Assert.Contains("GATEWAY_JOB_NAME=mirror", created.Env);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        var lines = result.Tail.Split('\n');
        Assert.Equal(20, lines.Length);
        Assert.Equal("line 6", lines[0]);
        Assert.Equal("line 25", lines[^1]);

        Assert.DoesNotContain("KillContainerAsync", docker.CallNames);
        Assert.Equal("RemoveContainerAsync", docker.CallNames.Last());
        var logs = docker.Containers.Calls.Single(c => c.Method == "GetContainerLogsAsync");
        Assert.Equal("20", ((ContainerLogsParameters)logs.Args[2]!).Tail);
    }

    [Fact]
    public async Task Run_KillsOnTimeout_AndStillRemoves()
    {
        var docker = new Docker
        {
            Wait = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ContainerWaitResponse();
            },
        };

        var result = await docker.Runtime().RunJobContainerAsync(Spec(), TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Null(result.ExitCode);
        Assert.Contains("KillContainerAsync", docker.CallNames);
        Assert.Equal("RemoveContainerAsync", docker.CallNames.Last());
    }

    [Fact]
    public async Task Run_KillsOnTheCancellationToken()
    {
        var docker = new Docker
        {
            Wait = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ContainerWaitResponse();
            },
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await docker.Runtime().RunJobContainerAsync(Spec(), TimeSpan.FromMinutes(5), cancel.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Contains("KillContainerAsync", docker.CallNames);
        Assert.Equal("RemoveContainerAsync", docker.CallNames.Last());
    }

    [Fact]
    public async Task Run_RemovesTheContainerWhenStartFails()
    {
        var docker = new Docker();
        docker.Containers.Handlers["StartContainerAsync"] = _ =>
            Task.FromException<bool>(new DockerApiException(System.Net.HttpStatusCode.InternalServerError, "start failed"));

        await Assert.ThrowsAsync<DockerApiException>(() =>
            docker.Runtime().RunJobContainerAsync(Spec(), TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal("RemoveContainerAsync", docker.CallNames.Last());
    }

    [Fact]
    public async Task Find_FiltersByTheRunLabel_AndWaitReturnsTheRealExitCode()
    {
        var docker = new Docker
        {
            Wait = _ => Task.FromResult(new ContainerWaitResponse { StatusCode = 7 }),
        };
        docker.Containers.Handlers["ListContainersAsync"] = _ => Task.FromResult<IList<ContainerListResponse>>(
            new List<ContainerListResponse>
            {
                new()
                {
                    ID = "c0ffee",
                    Names = new List<string> { "/job-mirror-42" },
                    State = "exited",
                    Labels = new Dictionary<string, string> { [ContainerLabels.Job] = "mirror", [ContainerLabels.JobRun] = "42" },
                },
            });
        docker.Containers.Handlers["InspectContainerAsync"] = _ => Task.FromResult(new ContainerInspectResponse
        {
            State = new ContainerState { StartedAt = "2026-01-05T12:00:00Z" },
        });
        var runtime = docker.Runtime();

        var found = await runtime.FindJobContainerAsync(42);

        var list = docker.Containers.Calls.First(c => c.Method == "ListContainersAsync");
        var filters = ((ContainersListParameters)list.Args[0]!).Filters;
        Assert.True(filters["label"].ContainsKey("gateway.job-run=42"));
        Assert.True(((ContainersListParameters)list.Args[0]!).All);
        Assert.NotNull(found);
        Assert.Equal("job-mirror-42", found!.Name);
        Assert.Equal("mirror", found.Job);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero), found.StartedAt);

        var result = await runtime.WaitJobContainerAsync(found, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("RemoveContainerAsync", docker.CallNames.Last());
    }

    [Fact]
    public async Task Find_ReturnsNullWhenNoContainerCarriesTheRunLabel()
    {
        var docker = new Docker();
        docker.Containers.Handlers["ListContainersAsync"] = _ =>
            Task.FromResult<IList<ContainerListResponse>>(new List<ContainerListResponse>());

        Assert.Null(await docker.Runtime().FindJobContainerAsync(42));
    }

    [Fact]
    public async Task ListManaged_SkipsAContainerCarryingTheJobLabel()
    {
        var docker = new Docker();
        docker.Containers.Handlers["ListContainersAsync"] = _ => Task.FromResult<IList<ContainerListResponse>>(
            new List<ContainerListResponse>
            {
                new()
                {
                    ID = "a",
                    Names = new List<string> { "/svc-a" },
                    State = "running",
                    Labels = new Dictionary<string, string> { [ContainerLabels.Managed] = "true" },
                },
                new()
                {
                    ID = "b",
                    Names = new List<string> { "/job-mirror-42" },
                    State = "running",
                    Labels = new Dictionary<string, string>
                    {
                        [ContainerLabels.Managed] = "true",
                        [ContainerLabels.Job] = "mirror",
                        [ContainerLabels.JobRun] = "42",
                    },
                },
            });
        docker.Containers.Handlers["InspectContainerAsync"] = _ => Task.FromResult(new ContainerInspectResponse
        {
            State = new ContainerState { StartedAt = "2026-01-05T12:00:00Z" },
        });

        var listed = await docker.Runtime().ListManagedContainersAsync();

        Assert.Equal(new[] { "svc-a" }, listed.Select(c => c.Name));
    }
}
