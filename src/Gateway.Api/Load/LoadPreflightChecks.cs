using Gateway.Api.Instances;
using Gateway.Api.Resources;

namespace Gateway.Api.Load;

/// <summary>
/// <c>generator_group</c>: green when the generator group has desired 0, nothing in
/// service, and a max that fits the run's generator count. Read through
/// <see cref="IFleetScaler"/> (the AWS one whenever <c>Load:GeneratorGroup</c> is set);
/// a group that cannot be read is red.
/// </summary>
public sealed class GeneratorGroupCheck : ILoadPreflightCheck
{
    private readonly IFleetScaler _scaler;

    public GeneratorGroupCheck(IFleetScaler scaler)
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

        FleetGroupSize? size;
        try
        {
            size = await _scaler.DescribeGroupAsync(group, ct);
        }
        catch (NotSupportedException ex)
        {
            return Red($"The generator group cannot be read: {ex.Message}", "Set Load:GeneratorGroup so the AWS fleet scaler is registered.");
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
/// endpoint reads them). When the fleet group is known (<see cref="FleetGroupResolver"/>)
/// and can be read through <see cref="IFleetScaler"/>, the group's in-service count
/// must not exceed the instances that heartbeat.
/// </summary>
public sealed class FleetHealthyCheck : ILoadPreflightCheck
{
    private readonly IFleetScaler _scaler;
    private readonly FleetGroupResolver _groups;

    public FleetHealthyCheck(IFleetScaler scaler, FleetGroupResolver groups)
    {
        _scaler = scaler;
        _groups = groups;
    }

    public string Name => "fleet_healthy";

    public async Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
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

        var group = await _groups.GetAsync(ct);
        if (group is not null)
        {
            FleetGroupSize? size = null;
            try
            {
                size = await _scaler.DescribeGroupAsync(group, ct);
            }
            catch (NotSupportedException)
            {
                // The null scaler: the heartbeats above are the whole check.
            }

            if (size is not null && size.InService > context.FreshInstances.Count)
            {
                return Red(
                    $"The fleet group has {size.InService} instances in service but {context.FreshInstances.Count} heartbeat.",
                    "Wait for the new instances to boot and report.");
            }
        }

        return new PreflightCheckResult(Name, PreflightStatus.Green,
            $"{context.FreshInstances.Count} fleet instances heartbeat and run {target}.", null);
    }

    private PreflightCheckResult Red(string message, string fix) => new(Name, PreflightStatus.Red, message, fix);
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

/// <summary>
/// <c>database_headroom</c>: green when
/// <c>max_connections - in use &gt;= added nodes * Load:DbConnectionsPerNode + Load:DbConnectionMargin</c>,
/// where added nodes is how many instances the run's requested fleet size would add
/// beyond those that heartbeat now (zero when no size is requested or it is not
/// larger): the live nodes' connections are already counted in "in use". Amber when
/// there is no database to ask.
/// </summary>
public sealed class DatabaseHeadroomCheck : ILoadPreflightCheck
{
    private readonly IDatabaseHeadroom _headroom;

    public DatabaseHeadroomCheck(IDatabaseHeadroom headroom)
    {
        _headroom = headroom;
    }

    public string Name => "database_headroom";

    public async Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
    {
        var connections = await _headroom.GetAsync(ct);
        if (connections is null)
        {
            return new PreflightCheckResult(Name, PreflightStatus.Amber, "Unknown: no database to ask.", null);
        }

        var live = context.FreshInstances.Count;
        var added = Math.Max(0, (context.Request.FleetSize ?? live) - live);
        var perNode = context.Options.DbConnectionsPerNode;
        var margin = context.Options.DbConnectionMargin;
        var needed = (added * perNode) + margin;
        var free = connections.MaxConnections - connections.InUse;
        var numbers = $"max_connections {connections.MaxConnections}, in use {connections.InUse}, free {free}; "
            + $"{live} live nodes, {added} added x {perNode} + {margin} needs {needed}.";

        return free >= needed
            ? new PreflightCheckResult(Name, PreflightStatus.Green, numbers, null)
            : new PreflightCheckResult(Name, PreflightStatus.Red, numbers,
                "Raise the database class on the Resources page (the database card), or ask for a smaller fleet.");
    }
}

/// <summary>
/// <c>fleet_class</c>: the fleet launch template's <c>$Default</c> instance type is
/// not a burstable family (<c>t2</c>, <c>t3</c>, <c>t3a</c>, <c>t4g</c>). Amber, never
/// red, in v1; amber "unknown" when <c>Resources:FleetTemplate</c> is unset or the
/// template cannot be read through <see cref="IFleetScaler"/> (the AWS one whenever the
/// template is set).
/// </summary>
public sealed class FleetClassCheck : ILoadPreflightCheck
{
    private static readonly string[] Burstable = { "t2", "t3", "t3a", "t4g" };

    private readonly IFleetScaler _scaler;
    private readonly ResourcesOptions _resources;

    public FleetClassCheck(IFleetScaler scaler, ResourcesOptions resources)
    {
        _scaler = scaler;
        _resources = resources;
    }

    public string Name => "fleet_class";

    public async Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
    {
        var template = _resources.FleetTemplate;
        if (template is null)
        {
            return Unknown("Resources:FleetTemplate is not set.");
        }

        FleetLaunchTemplate? described;
        try
        {
            described = await _scaler.DescribeLaunchTemplateAsync(template, ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or Amazon.Runtime.AmazonServiceException)
        {
            return Unknown(ex.Message);
        }

        if (described is null)
        {
            return Unknown($"the launch template {template} does not exist.");
        }

        if (described.InstanceType is not { Length: > 0 } instanceType)
        {
            return Unknown("the template's $Default sets no instance type.");
        }

        var family = instanceType.Split('.')[0];
        return Burstable.Contains(family, StringComparer.OrdinalIgnoreCase)
            ? new PreflightCheckResult(Name, PreflightStatus.Amber,
                $"The fleet runs {instanceType}, a burstable class: CPU credits can run out under sustained load.",
                "Change the fleet class on the Resources page for a large run.")
            : new PreflightCheckResult(Name, PreflightStatus.Green, $"The fleet runs {instanceType}.", null);
    }

    private PreflightCheckResult Unknown(string why) =>
        new(Name, PreflightStatus.Amber, $"Unknown: {why}", null);
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
