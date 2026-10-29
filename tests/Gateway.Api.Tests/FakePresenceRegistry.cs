using Gateway.Api.RealTime;

namespace Gateway.Api.Tests;

/// <summary>
/// <see cref="IPresenceRegistry"/> for tests that only read member counts: a channel's
/// count is whatever <see cref="Counts"/> holds (0 when absent), and every count read is
/// recorded in <see cref="CountReads"/>. Membership writes are ignored.
/// </summary>
public sealed class FakePresenceRegistry : IPresenceRegistry
{
    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    public List<string> CountReads { get; } = new();

    public Task AddAsync(string channel, string connectionId, string? identity, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveAsync(string channel, string connectionId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyCollection<string>> RemoveConnectionAsync(string connectionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<PresenceEntry>> ListAsync(string channel, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PresenceEntry>>(Array.Empty<PresenceEntry>());

    public Task<int> CountAsync(string channel, CancellationToken ct = default)
    {
        CountReads.Add(channel);
        return Task.FromResult(Counts.TryGetValue(channel, out var n) ? n : 0);
    }

    public Task<IReadOnlyList<ChannelMembership>> LocalMembershipsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChannelMembership>>(Array.Empty<ChannelMembership>());
}
