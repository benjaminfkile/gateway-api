using Gateway.Api.Instances;

namespace Gateway.Api.Load;

/// <summary>
/// <c>generator_group</c>: green when the generator group has desired 0, nothing in
/// service, and a max that fits the run's generator count. Read through
/// <see cref="IGeneratorGroupScaler"/> (the AWS one whenever <c>Load:GeneratorGroup</c>
/// is set); a group that cannot be read is red.
/// </summary>
public sealed class GeneratorGroupCheck : ILoadPreflightCheck
{
    private readonly IGeneratorGroupScaler _scaler;

    public GeneratorGroupCheck(IGeneratorGroupScaler scaler)
    {
        _scaler = scaler;
    }

    public string Name => "generator_group";

    public async Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
    {
        var group = context.Options.GeneratorGroup;
        if (string.IsNullOrWhiteSpace(group))
        {
            return Red("Load:GeneratorGroup is not set.", "Set Load:GeneratorGroup to the generator Auto Scaling group name.");
        }

        GeneratorGroupSize? size;
        try
        {
            size = await _scaler.DescribeAsync(group, ct);
        }
        catch (NotSupportedException ex)
        {
            return Red($"The generator group cannot be read: {ex.Message}", "Set Load:GeneratorGroup so the AWS generator group scaler is registered.");
        }

        if (size is null)
        {
            return Red($"The generator group {group} does not exist.", "Create the generator stack, or fix Load:GeneratorGroup.");
        }

        if (size.Desired > 0 || size.InService > 0)
        {
            return Red(
                $"The generator group has desired {size.Desired} and {size.InService} in service; a previous run left instances.",
                "Set the generator group's desired capacity to 0 and wait for the instances to terminate.");
        }

        var generators = context.Request.Validation ? 1 : context.Request.Generators;
        if (generators > size.Max)
        {
            return Red(
                $"The run asks for {generators} generators but the generator group's max is {size.Max}.",
                "Ask for fewer generators, or raise the group's max in the generator stack.");
        }

        return new PreflightCheckResult(Name, PreflightStatus.Green,
            $"The generator group is empty and fits {generators} of max {size.Max}.", null);
    }

    private PreflightCheckResult Red(string message, string fix) => new(Name, PreflightStatus.Red, message, fix);
}

/// <summary>
/// <c>fleet_healthy</c>: green when every <c>instance_status</c> row has a fresh
/// heartbeat and the target service is running on each (read the way the Instances
/// endpoint reads them).
/// </summary>
public sealed class FleetHealthyCheck : ILoadPreflightCheck
{
    public string Name => "fleet_healthy";

    public Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
    {
        if (context.StaleInstances.Count > 0)
        {
            var names = string.Join(", ", context.StaleInstances.Select(i => i.InstanceId));
            return Red(
                $"No heartbeat within {context.StaleThreshold.TotalSeconds:0} s from {names}.",
                "Wait for the instance to recover or be replaced and its row to age out.");
        }

        if (context.FreshInstances.Count == 0)
        {
            return Red("No fleet instance has a fresh heartbeat.", "Check that the gateway reconciler is running on the fleet.");
        }

        var target = context.Request.Target ?? string.Empty;
        var missing = context.FreshInstances
            .Where(i => !InstanceServicesJson.Parse(i.Services).Any(s =>
                string.Equals(s.Name, target, StringComparison.Ordinal)
                && string.Equals(s.State, "running", StringComparison.Ordinal)))
            .Select(i => i.InstanceId)
            .ToList();
        if (missing.Count > 0)
        {
            return Red(
                $"The target {target} is not running on {string.Join(", ", missing)}.",
                "Start or deploy the target service and wait for every instance to converge.");
        }

        return Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Green,
            $"{context.FreshInstances.Count} fleet instances heartbeat and run {target}.", null));
    }

    private Task<PreflightCheckResult> Red(string message, string fix) =>
        Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Red, message, fix));
}

/// <summary><c>target_known</c>: green when the target is a manifest service.</summary>
public sealed class TargetKnownCheck : ILoadPreflightCheck
{
    public string Name => "target_known";

    public Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default) =>
        Task.FromResult(context.TargetService is not null
            ? new PreflightCheckResult(Name, PreflightStatus.Green, $"{context.TargetService.Name} is a manifest service.", null)
            : new PreflightCheckResult(Name, PreflightStatus.Red,
                $"{(string.IsNullOrWhiteSpace(context.Request.Target) ? "No target" : context.Request.Target)} is not a manifest service.",
                "Pick a service from the Services page."));
}

/// <summary>
/// <c>scenario_validated</c>: green when the scenario's last validation run is set and
/// ran the current script version. Always green for a validation run.
/// </summary>
public sealed class ScenarioValidatedCheck : ILoadPreflightCheck
{
    public string Name => "scenario_validated";

    public Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
    {
        var scenario = context.Scenario;
        if (scenario is null)
        {
            return Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Red,
                $"{(string.IsNullOrWhiteSpace(context.Request.Scenario) ? "No scenario" : context.Request.Scenario)} is not a registered scenario.",
                "Register the scenario with PUT /mgmt/load/scenarios/{name}."));
        }

        if (context.Request.Validation)
        {
            return Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Green,
                "Skipped for a validation run.", null));
        }

        if (scenario.LastValidationRunId is null)
        {
            return Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Red,
                $"Script version {scenario.ScriptVersion} of {scenario.Name} has not been validated.",
                "Validate first: start a validation run."));
        }

        if (context.ValidationRun is null || context.ValidationRun.ScenarioVersion != scenario.ScriptVersion)
        {
            return Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Red,
                $"The last validation of {scenario.Name} ran another script version than {scenario.ScriptVersion}.",
                "Validate first: start a validation run."));
        }

        return Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Green,
            $"Script version {scenario.ScriptVersion} passed validation run {context.ValidationRun.Id}.", null));
    }
}

/// <summary><c>presence_off</c>: green when the target's <c>realtime_presence</c> is not on.</summary>
public sealed class PresenceOffCheck : ILoadPreflightCheck
{
    public string Name => "presence_off";

    public Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
    {
        var service = context.TargetService;
        if (service is null)
        {
            return Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Amber,
                "Unknown: the target is not a manifest service.", null));
        }

        return Task.FromResult(service.RealtimePresence == true
            ? new PreflightCheckResult(Name, PreflightStatus.Red,
                $"{service.Name} has realtime presence on: a join batch would broadcast to every member.",
                "Turn realtime presence off for the service before the run.")
            : new PreflightCheckResult(Name, PreflightStatus.Green, $"{service.Name} has realtime presence off.", null));
    }
}

/// <summary><c>target_confirmed</c>: green when <c>confirm</c> equals the target name exactly.</summary>
public sealed class TargetConfirmedCheck : ILoadPreflightCheck
{
    public string Name => "target_confirmed";

    public Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
    {
        var target = context.Request.Target;
        var confirmed = !string.IsNullOrEmpty(target) && string.Equals(context.Request.Confirm, target, StringComparison.Ordinal);
        return Task.FromResult(confirmed
            ? new PreflightCheckResult(Name, PreflightStatus.Green, $"The target {target} is confirmed.", null)
            : new PreflightCheckResult(Name, PreflightStatus.Red,
                "The target is not confirmed.",
                "Type the target name to confirm; production is allowed."));
    }
}
