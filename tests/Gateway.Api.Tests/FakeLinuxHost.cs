using Gateway.Api.Bootstrap;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="ILinuxHost"/> for bootstrap-step tests: the build box has no
/// root filesystem to mutate and no Docker daemon. Models an in-memory filesystem
/// and just enough Docker-network state that <c>docker network inspect</c> succeeds
/// only after a <c>create</c> — so the network step's idempotency is exercised
/// end-to-end. Every command is recorded in <see cref="Commands"/> for assertions,
/// and a <see cref="Handler"/> can override any process result (e.g. to force a
/// failure).
/// </summary>
public sealed class FakeLinuxHost : ILinuxHost
{
    private readonly object _gate = new();

    /// <summary>The in-memory filesystem (path → content).</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Docker networks that currently exist.</summary>
    public HashSet<string> Networks { get; } = new(StringComparer.Ordinal);

    /// <summary>Every process invocation, in order.</summary>
    public List<CommandInvocation> Commands { get; } = new();

    /// <summary>Optional override: return a non-null result to bypass the default simulation.</summary>
    public Func<string, IReadOnlyList<string>, string?, ProcessResult?>? Handler { get; set; }

    public Task<string?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Files.TryGetValue(path, out var content) ? content : null);
        }
    }

    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Files[path] = content;
        }

        return Task.CompletedTask;
    }

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var args = arguments.ToArray();
            Commands.Add(new CommandInvocation(executable, args, standardInput));

            var overridden = Handler?.Invoke(executable, args, standardInput);
            return Task.FromResult(overridden ?? Simulate(executable, args));
        }
    }

    /// <summary>Default process behaviour: model docker networks, succeed otherwise.</summary>
    private ProcessResult Simulate(string executable, IReadOnlyList<string> args)
    {
        if (executable == "docker" && args.Count >= 3 && args[0] == "network")
        {
            var name = args[^1];
            switch (args[1])
            {
                case "inspect":
                    return Networks.Contains(name) ? Ok() : Fail($"Error: No such network: {name}");
                case "create":
                    Networks.Add(name);
                    return Ok();
            }
        }

        return Ok();
    }

    private static ProcessResult Ok() => new(0, string.Empty, string.Empty);

    private static ProcessResult Fail(string error) => new(1, string.Empty, error);
}

/// <summary>A single recorded process invocation.</summary>
public sealed record CommandInvocation(string Executable, string[] Arguments, string? StandardInput);
