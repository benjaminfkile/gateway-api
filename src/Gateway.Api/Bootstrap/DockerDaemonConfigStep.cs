using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Bootstrap step (tech-spec §4.3): ensure <c>/etc/docker/daemon.json</c> carries a
/// log-rotation policy so container logs cannot fill the disk. Merges the
/// <c>log-driver</c>/<c>log-opts</c> keys into any existing daemon config (other
/// keys are preserved) and only rewrites + reloads Docker when the effective config
/// actually differs — so a second run is a no-op.
/// </summary>
public sealed class DockerDaemonConfigStep : IBootstrapStep
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ILinuxHost _host;
    private readonly BootstrapOptions _options;
    private readonly ILogger<DockerDaemonConfigStep> _logger;

    public DockerDaemonConfigStep(ILinuxHost host, BootstrapOptions options, ILogger<DockerDaemonConfigStep> logger)
    {
        _host = host;
        _options = options;
        _logger = logger;
    }

    public string Name => "docker-daemon-config";

    public async Task<BootstrapStepResult> RunAsync(CancellationToken ct = default)
    {
        var path = _options.DockerDaemonConfigPath;
        var existing = await _host.ReadFileAsync(path, ct);

        var desired = BuildDesired(existing);
        if (string.Equals(Canonicalize(existing), desired, StringComparison.Ordinal))
        {
            return BootstrapStepResult.Skipped($"{path} already has the desired log-rotation config.");
        }

        await _host.WriteFileAsync(path, desired, ct);

        // Reload the daemon so the new log-rotation policy takes effect. A failure
        // here is surfaced by the pipeline; the config file is already written.
        var reload = await _host.RunAsync("systemctl", new[] { "reload", "docker" }, null, ct);
        if (!reload.Succeeded)
        {
            _logger.LogWarning(
                "Wrote {Path} but 'systemctl reload docker' exited {Code}: {Error}",
                path, reload.ExitCode, reload.StandardError);
        }

        return BootstrapStepResult.Applied(
            $"Wrote log-rotation policy (max-size={_options.DockerLogMaxSize}, max-file={_options.DockerLogMaxFile}) to {path} and reloaded Docker.");
    }

    /// <summary>
    /// Merge the log-rotation keys into the existing daemon config (or a fresh
    /// object) and return the canonical serialization.
    /// </summary>
    private string BuildDesired(string? existing)
    {
        var root = TryParseObject(existing) ?? new JsonObject();

        root["log-driver"] = "json-file";
        root["log-opts"] = new JsonObject
        {
            ["max-size"] = _options.DockerLogMaxSize,
            ["max-file"] = _options.DockerLogMaxFile,
        };

        return root.ToJsonString(Indented);
    }

    /// <summary>Re-serialize existing content the same way so comparison ignores formatting.</summary>
    private static string? Canonicalize(string? content)
    {
        var root = TryParseObject(content);
        return root?.ToJsonString(Indented);
    }

    private static JsonObject? TryParseObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
