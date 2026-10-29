using Gateway.Api.Resources;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IFleetScaler"/> for tests: groups and launch templates are
/// dictionaries a test fills in, every mutation is recorded, and a name in
/// <see cref="Failing"/> throws <see cref="NotSupportedException"/> the way
/// <see cref="NullFleetScaler"/> does.
/// </summary>
public sealed class FakeFleetScaler : IFleetScaler
{
    private readonly object _gate = new();

    public Dictionary<string, FleetGroupSize> Groups { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FleetLaunchTemplate> Templates { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = new();

    public Task<FleetGroupSize?> DescribeGroupAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(name);
            Calls.Add($"describe-group {name}");
            return Task.FromResult(Groups.TryGetValue(name, out var size) ? size : null);
        }
    }

    public Task SetGroupSizeAsync(string name, int min, int desired, int max, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(name);
            Calls.Add($"set-group-size {name} {min} {desired} {max}");
            var inService = Groups.TryGetValue(name, out var size) ? size.InService : 0;
            Groups[name] = new FleetGroupSize(min, desired, max, inService);
            return Task.CompletedTask;
        }
    }

    public Task SetDesiredAsync(string name, int desired, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(name);
            Calls.Add($"set-desired {name} {desired}");
            if (Groups.TryGetValue(name, out var size))
            {
                Groups[name] = size with { Desired = desired };
            }

            return Task.CompletedTask;
        }
    }

    public Task<FleetLaunchTemplate?> DescribeLaunchTemplateAsync(string template, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(template);
            Calls.Add($"describe-template {template}");
            return Task.FromResult(Templates.TryGetValue(template, out var t) ? t : null);
        }
    }

    public Task<long> CreateLaunchTemplateVersionAsync(string template, string instanceType, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(template);
            Calls.Add($"create-template-version {template} {instanceType}");
            var current = Templates.TryGetValue(template, out var t) ? t.DefaultVersion : 0;
            return Task.FromResult(current + 1);
        }
    }

    public Task SetDefaultLaunchTemplateVersionAsync(string template, long version, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(template);
            Calls.Add($"set-default-version {template} {version}");
            return Task.CompletedTask;
        }
    }

    public Task<string> StartInstanceRefreshAsync(string group, FleetRefreshPreferences preferences, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(group);
            Calls.Add($"start-refresh {group}");
            return Task.FromResult("refresh-placeholder");
        }
    }

    public Task CancelInstanceRefreshAsync(string group, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(group);
            Calls.Add($"cancel-refresh {group}");
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<FleetRefresh>> DescribeInstanceRefreshesAsync(string group, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(group);
            Calls.Add($"describe-refreshes {group}");
            return Task.FromResult<IReadOnlyList<FleetRefresh>>(Array.Empty<FleetRefresh>());
        }
    }

    private void Fail(string name)
    {
        if (Failing.Contains(name))
        {
            throw new NotSupportedException(NullFleetScaler.NotConfiguredMessage);
        }
    }
}
