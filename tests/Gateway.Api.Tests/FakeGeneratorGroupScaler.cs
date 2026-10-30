using Gateway.Api.Load;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IGeneratorGroupScaler"/> for tests, scripted through
/// <see cref="Groups"/>. Every call is recorded in <see cref="Calls"/>; a name in
/// <see cref="Failing"/> throws <see cref="NotSupportedException"/> the way
/// <see cref="NullGeneratorGroupScaler"/> does.
/// </summary>
public sealed class FakeGeneratorGroupScaler : IGeneratorGroupScaler
{
    private readonly object _gate = new();

    public Dictionary<string, GeneratorGroupSize> Groups { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = new();

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

    public Task<GeneratorGroupSize?> DescribeAsync(string group, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(group);
            Calls.Add($"describe-group {group}");
            return Task.FromResult(Groups.TryGetValue(group, out var size) ? size : null);
        }
    }

    public Task SetDesiredAsync(string group, int desired, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Fail(group);
            Calls.Add($"set-desired {group} {desired}");
            if (Groups.TryGetValue(group, out var size))
            {
                Groups[group] = size with { Desired = desired };
            }

            return Task.CompletedTask;
        }
    }

    private void Fail(string group)
    {
        if (Failing.Contains(group))
        {
            throw new NotSupportedException(NullGeneratorGroupScaler.NotConfiguredMessage);
        }
    }
}
