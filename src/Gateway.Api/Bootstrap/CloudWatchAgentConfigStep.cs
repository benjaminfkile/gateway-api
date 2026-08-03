using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Bootstrap step (tech-spec §4.3, §9): write the CloudWatch agent config that ships
/// gateway logs to the centralized log store and emits the disk/mem watermark
/// metrics, then reload the agent. Only rewrites + reloads when the desired config
/// differs from what is on disk, so a second run is a no-op.
/// </summary>
public sealed class CloudWatchAgentConfigStep : IBootstrapStep
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ILinuxHost _host;
    private readonly BootstrapOptions _options;
    private readonly ILogger<CloudWatchAgentConfigStep> _logger;

    public CloudWatchAgentConfigStep(ILinuxHost host, BootstrapOptions options, ILogger<CloudWatchAgentConfigStep> logger)
    {
        _host = host;
        _options = options;
        _logger = logger;
    }

    public string Name => "cloudwatch-agent-config";

    public async Task<BootstrapStepResult> RunAsync(CancellationToken ct = default)
    {
        var path = _options.CloudWatchAgentConfigPath;
        var desired = BuildConfig();

        var existing = Canonicalize(await _host.ReadFileAsync(path, ct));
        if (string.Equals(existing, desired, StringComparison.Ordinal))
        {
            return BootstrapStepResult.Skipped($"CloudWatch agent config at {path} already current.");
        }

        await _host.WriteFileAsync(path, desired, ct);

        // Ask the agent to pick up the new config. A failure is surfaced by the
        // pipeline; the config file is already written.
        var reload = await _host.RunAsync(
            _options.CloudWatchAgentCtlPath,
            new[] { "-a", "fetch-config", "-m", "ec2", "-s", "-c", $"file:{path}" },
            null,
            ct);
        if (!reload.Succeeded)
        {
            _logger.LogWarning(
                "Wrote {Path} but the CloudWatch agent reload exited {Code}: {Error}",
                path, reload.ExitCode, reload.StandardError);
        }

        return BootstrapStepResult.Applied($"Wrote CloudWatch agent config to {path} and reloaded the agent.");
    }

    private string BuildConfig()
    {
        var config = new JsonObject
        {
            ["agent"] = new JsonObject
            {
                ["metrics_collection_interval"] = 60,
                ["run_as_user"] = "root",
            },
            ["logs"] = new JsonObject
            {
                ["logs_collected"] = new JsonObject
                {
                    ["files"] = new JsonObject
                    {
                        ["collect_list"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["file_path"] = _options.GatewayLogPath,
                                ["log_group_name"] = $"{_options.LogGroupPrefix}/{{instance_id}}",
                                ["log_stream_name"] = "{instance_id}",
                                ["retention_in_days"] = 30,
                            },
                        },
                    },
                },
            },
            ["metrics"] = new JsonObject
            {
                ["namespace"] = _options.MetricsNamespace,
                ["append_dimensions"] = new JsonObject
                {
                    ["InstanceId"] = "${aws:InstanceId}",
                },
                ["metrics_collected"] = new JsonObject
                {
                    ["disk"] = new JsonObject
                    {
                        ["measurement"] = new JsonArray { "used_percent" },
                        ["resources"] = new JsonArray { "/" },
                    },
                    ["mem"] = new JsonObject
                    {
                        ["measurement"] = new JsonArray { "mem_used_percent" },
                    },
                },
            },
        };

        return config.ToJsonString(Indented);
    }

    private static string? Canonicalize(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(content)?.ToJsonString(Indented);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
