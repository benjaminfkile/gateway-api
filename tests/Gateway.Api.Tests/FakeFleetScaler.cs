using Gateway.Api.Resources;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IFleetScaler"/> for tests, scripted with three kinds of state:
/// groups (<see cref="Groups"/>), launch templates (<see cref="Templates"/> holds each
/// template's <c>$Default</c>, <see cref="Versions"/> every version) and instance
/// refreshes (<see cref="Refreshes"/>, newest first per group). Every call is recorded
/// in <see cref="Calls"/>; a name in <see cref="Failing"/> throws
/// <see cref="NotSupportedException"/> the way <see cref="NullFleetScaler"/> does, and
/// <see cref="StartRefreshError"/> makes the refresh start throw.
/// </summary>
public sealed class FakeFleetScaler : IFleetScaler
{
    private readonly object _gate = new();
    private int _refreshes;

    public Dictionary<string, FleetGroupSize> Groups { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FleetLaunchTemplate> Templates { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<long, FleetTemplateVersion>> Versions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<FleetRefresh>> Refreshes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = new();

    /// <summary>When set, <see cref="StartInstanceRefreshAsync"/> throws it.</summary>
    public Exception? StartRefreshError { get; set; }

    /// <summary>The calls recorded so far, as a snapshot.</summary>
    public IReadOnlyList<string> CallLog
    {
        get
        {
            lock (_gate)
            {
                return Calls.ToList();
            }
        }
    }

    /// <summary>Seed a template whose <c>$Default</c> is <paramref name="version"/> with <paramref name="instanceType"/>.</summary>
    public void SeedTemplate(string template, long version, string instanceType)
    {
        lock (_gate)
        {
            Templates[template] = new FleetLaunchTemplate("lt-0placeholder", version, instanceType, $"version {version}");
            VersionsOf(template)[version] = new FleetTemplateVersion(version, instanceType, $"version {version}");
        }
    }

    /// <summary>Script a refresh of <paramref name="group"/> into <paramref name="status"/>.</summary>
    public void SetRefresh(string group, string refreshId, string status, int? percentComplete = null)
    {
        lock (_gate)
        {
            var list = RefreshesOf(group);
            var index = list.FindIndex(r => r.RefreshId == refreshId);
            var refresh = new FleetRefresh(refreshId, status, percentComplete);
            if (index >= 0)
            {
                list[index] = refresh;
            }
            else
            {
                list.Insert(0, refresh);
            }
        }
    }

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

    public Task<FleetTemplateVersion?> DescribeLaunchTemplateVersionAsync(string template, long version, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(template);
            Calls.Add($"describe-template-version {template} {version}");
            return Task.FromResult(Versions.TryGetValue(template, out var versions) && versions.TryGetValue(version, out var v) ? v : null);
        }
    }

    public Task<long> CreateLaunchTemplateVersionAsync(string template, string instanceType, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(template);
            Calls.Add($"create-template-version {template} from $Default InstanceType={instanceType}");
            var versions = VersionsOf(template);
            var current = Templates.TryGetValue(template, out var t) ? t.DefaultVersion : 0;
            var next = Math.Max(current, versions.Count == 0 ? 0 : versions.Keys.Max()) + 1;
            versions[next] = new FleetTemplateVersion(next, instanceType, null);
            return Task.FromResult(next);
        }
    }

    public Task SetDefaultLaunchTemplateVersionAsync(string template, long version, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(template);
            Calls.Add($"set-default-version {template} {version}");
            if (VersionsOf(template).TryGetValue(version, out var v))
            {
                var id = Templates.TryGetValue(template, out var t) ? t.TemplateId : "lt-0placeholder";
                Templates[template] = new FleetLaunchTemplate(id, version, v.InstanceType, v.Description);
            }

            return Task.CompletedTask;
        }
    }

    public Task<string> StartInstanceRefreshAsync(string group, FleetRefreshPreferences preferences, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(group);
            Calls.Add($"start-refresh {group} min-healthy {preferences.MinHealthyPercentage} max-healthy {preferences.MaxHealthyPercentage} warmup {preferences.InstanceWarmupSeconds}");
            if (StartRefreshError is { } error)
            {
                throw error;
            }

            var id = $"refresh-placeholder-{++_refreshes}";
            RefreshesOf(group).Insert(0, new FleetRefresh(id, "Pending", 0));
            return Task.FromResult(id);
        }
    }

    public Task CancelInstanceRefreshAsync(string group, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(group);
            Calls.Add($"cancel-refresh {group}");
            var list = RefreshesOf(group);
            if (list.Count > 0 && !list[0].IsTerminal)
            {
                list[0] = list[0] with { Status = "Cancelled" };
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<FleetRefresh>> DescribeInstanceRefreshesAsync(string group, string? refreshId = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(group);
            Calls.Add(refreshId is null ? $"describe-refreshes {group}" : $"describe-refreshes {group} {refreshId}");
            IReadOnlyList<FleetRefresh> list = RefreshesOf(group)
                .Where(r => refreshId is null || r.RefreshId == refreshId)
                .ToList();
            return Task.FromResult(list);
        }
    }

    private Dictionary<long, FleetTemplateVersion> VersionsOf(string template)
    {
        if (!Versions.TryGetValue(template, out var versions))
        {
            versions = new Dictionary<long, FleetTemplateVersion>();
            Versions[template] = versions;
        }

        return versions;
    }

    private List<FleetRefresh> RefreshesOf(string group)
    {
        if (!Refreshes.TryGetValue(group, out var list))
        {
            list = new List<FleetRefresh>();
            Refreshes[group] = list;
        }

        return list;
    }

    private void Fail(string name)
    {
        if (Failing.Contains(name))
        {
            throw new NotSupportedException(NullFleetScaler.NotConfiguredMessage);
        }
    }
}
