using Gateway.Api.RealTime;
using Microsoft.AspNetCore.SignalR;

namespace Gateway.Api.Tests;

/// <summary>
/// Hand-rolled <see cref="IHubContext{GatewayHub}"/> fake (this project carries no
/// Moq). Records every group send as (group, method, arg) so envelope tests can
/// assert the ChannelEvent wire shape, and can be told to throw from
/// <c>SendCoreAsync</c> to simulate a Redis backplane outage — proving
/// <see cref="ChannelEventPublisher.TryPublish"/> swallows it and a committed deploy
/// still returns success.
/// </summary>
public sealed class FakeGatewayHubContext : IHubContext<GatewayHub>
{
    /// <summary>Every group send, in order: the target group, method name, and single argument.</summary>
    public List<(string Group, string Method, object? Arg)> Sends { get; } = new();

    /// <summary>
    /// Every targeted single-connection send (the eviction sweep's <c>channelEvicted</c>
    /// notice, task #613): the connection id, method name, and single argument.
    /// </summary>
    public List<(string ConnectionId, string Method, object? Arg)> ClientSends { get; } = new();

    /// <summary>Every <c>RemoveFromGroupAsync</c> call, in order (the eviction sweep records here).</summary>
    public List<(string ConnectionId, string Group)> GroupRemovals { get; } = new();

    /// <summary>When set, every <c>SendCoreAsync</c> throws it (simulates a backplane blip).</summary>
    public Exception? SendError { get; set; }

    private readonly FakeHubClients _clients;

    public FakeGatewayHubContext()
    {
        _clients = new FakeHubClients(this);
        Groups = new FakeGroupManager(this);
    }

    public IHubClients Clients => _clients;

    public IGroupManager Groups { get; }

    private void Record(string group, string method, object?[] args) =>
        Sends.Add((group, method, args.Length > 0 ? args[0] : null));

    private void RecordClient(string connectionId, string method, object?[] args) =>
        ClientSends.Add((connectionId, method, args.Length > 0 ? args[0] : null));

    private sealed class FakeHubClients : IHubClients
    {
        private readonly FakeGatewayHubContext _owner;

        public FakeHubClients(FakeGatewayHubContext owner) => _owner = owner;

        public IClientProxy Group(string groupName) => new FakeClientProxy(_owner, groupName);

        // The eviction sweep (task #613) sends a targeted channelEvicted notice to a single
        // connection via Client(connectionId).
        public IClientProxy Client(string connectionId) => new FakeSingleClientProxy(_owner, connectionId);

        // The publisher/eviction sweep only fan out via Group(...) and Client(...); the rest
        // of the surface is never exercised, so leave it explicitly unsupported.
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class FakeClientProxy : IClientProxy
    {
        private readonly FakeGatewayHubContext _owner;
        private readonly string _group;

        public FakeClientProxy(FakeGatewayHubContext owner, string group)
        {
            _owner = owner;
            _group = group;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (_owner.SendError is not null)
            {
                throw _owner.SendError;
            }

            _owner.Record(_group, method, args);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSingleClientProxy : IClientProxy
    {
        private readonly FakeGatewayHubContext _owner;
        private readonly string _connectionId;

        public FakeSingleClientProxy(FakeGatewayHubContext owner, string connectionId)
        {
            _owner = owner;
            _connectionId = connectionId;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (_owner.SendError is not null)
            {
                throw _owner.SendError;
            }

            _owner.RecordClient(_connectionId, method, args);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGroupManager : IGroupManager
    {
        private readonly FakeGatewayHubContext _owner;

        public FakeGroupManager(FakeGatewayHubContext owner) => _owner = owner;

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            _owner.GroupRemovals.Add((connectionId, groupName));
            return Task.CompletedTask;
        }
    }
}
