using Gateway.Api.Resources;

namespace Gateway.Api.Tests;

/// <summary>
/// <see cref="IFleetGroupSource"/> for tests: answers <see cref="Group"/> (null by
/// default, as off EC2) and counts the reads, so no test reaches the metadata service.
/// </summary>
public sealed class FakeFleetGroupSource : IFleetGroupSource
{
    public string? Group { get; set; }

    public int Reads { get; private set; }

    public Task<string?> TryGetAsync(CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(Group);
    }
}
