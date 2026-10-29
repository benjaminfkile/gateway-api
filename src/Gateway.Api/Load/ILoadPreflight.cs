using Gateway.Api.Data;

namespace Gateway.Api.Load;

/// <summary>The three colours of a pre-flight check (docs/load.md section 6).</summary>
public static class PreflightStatus
{
    public const string Green = "green";
    public const string Amber = "amber";
    public const string Red = "red";
}

/// <summary>One pre-flight answer: <c>{ name, status, message, fix }</c>.</summary>
/// <param name="Name">The check name, e.g. <c>database_headroom</c>.</param>
/// <param name="Status"><c>green</c>, <c>amber</c> or <c>red</c> (see <see cref="PreflightStatus"/>).</param>
/// <param name="Message">What the check found.</param>
/// <param name="Fix">What to do about it, or null when nothing is needed.</param>
public sealed record PreflightCheckResult(string Name, string Status, string Message, string? Fix);

/// <summary>The run a pre-flight is evaluated for: the Start body, or the page's draft of it.</summary>
/// <param name="Scenario">Scenario name.</param>
/// <param name="Target">Target manifest service name.</param>
/// <param name="Confirm">What the operator typed to confirm the target.</param>
/// <param name="Generators">Generator count.</param>
/// <param name="VusPerGenerator">Virtual users per generator.</param>
/// <param name="FleetSize">Requested fleet size, or null to leave the fleet alone.</param>
/// <param name="Validation">True for a validation run.</param>
public sealed record LoadPreflightRequest(
    string? Scenario,
    string? Target,
    string? Confirm,
    int Generators,
    int VusPerGenerator,
    int? FleetSize,
    bool Validation);

/// <summary>
/// What every check reads, loaded once per evaluation: the request, the scenario and
/// its last validation run, the target's manifest row, and the fleet's
/// <c>instance_status</c> rows split by heartbeat freshness (the same stale threshold
/// the Instances endpoint uses).
/// </summary>
public sealed class LoadPreflightContext
{
    public required LoadPreflightRequest Request { get; init; }
    public required LoadOptions Options { get; init; }
    public required DateTimeOffset Now { get; init; }
    public LoadScenario? Scenario { get; init; }
    public LoadRun? ValidationRun { get; init; }
    public ServiceManifest? TargetService { get; init; }
    public required IReadOnlyList<InstanceStatus> FreshInstances { get; init; }
    public required IReadOnlyList<InstanceStatus> StaleInstances { get; init; }
    public required TimeSpan StaleThreshold { get; init; }
}

/// <summary>One pre-flight evaluator (docs/load.md section 6).</summary>
public interface ILoadPreflightCheck
{
    /// <summary>The check name the result carries.</summary>
    string Name { get; }

    /// <summary>Evaluate the check against <paramref name="context"/>.</summary>
    Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default);
}

/// <summary>
/// Runs every registered <see cref="ILoadPreflightCheck"/> for a run: the page's
/// <c>GET /mgmt/load/preflight</c>, <c>POST /mgmt/load/runs</c>, and the leader again
/// at claim time.
/// </summary>
public interface ILoadPreflight
{
    /// <summary>Every check's answer, in registration order. A check that throws answers red.</summary>
    Task<IReadOnlyList<PreflightCheckResult>> EvaluateAsync(LoadPreflightRequest request, CancellationToken ct = default);
}
