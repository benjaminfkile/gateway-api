using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Manifest;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Tests;

/// <summary>
/// End-to-end tests for the leadership-answer endpoint (task #217):
/// <c>GET /internal/leader</c>. Stood up over real Kestrel with two ports exactly the
/// way <see cref="InternalPublishTests"/> and <see cref="PresenceOwnerApiTests"/> are
/// so the internal-port isolation the middleware enforces on <c>/internal/*</c> can be
/// exercised, and so the endpoint is bound to the same
/// <see cref="RealtimeApplicationExtensions.MapInternalLeader"/> mapping the host uses.
/// </summary>
public sealed class InternalLeaderApiTests
{
    private const string TokenA = "token-a-secret";
    private const string TokenB = "token-b-secret";
    private const string ThisInstanceId = "i-under-test";

    private sealed record Gateway(WebApplication App, int PublicPort, int InternalPort, LeadershipState State)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ServiceManifest Owned(string name, string token) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Port = 8080,
        DesiredStatus = "running",
        RealtimePublishToken = token,
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static async Task<Gateway> StartGatewayAsync()
    {
        var publicPort = GetFreePort();
        var internalPort = GetFreePort();

        var config = new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{publicPort}",
            [InternalListenerOptions.BindEnvVar] = $"127.0.0.1:{internalPort}",
        };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(config);

        builder.AddGatewayInternalListener();
        builder.Services.AddGatewayRealtime(builder.Configuration);
        builder.Services.AddSingleton<IManifestStore>(new InMemoryManifestStore(new[]
        {
            Owned("svc-a", TokenA),
            Owned("svc-b", TokenB),
        }));

        // The endpoint reads instance id through InstanceMetadataProvider and leadership
        // through LeadershipState; register both against a fixed-identity stub so tests
        // can control what the answer includes without wiring the whole reconciler.
        builder.Services.AddSingleton<IInstanceMetadata>(
            new StubInstanceMetadata(new InstanceIdentity(ThisInstanceId, null, null)));
        builder.Services.AddSingleton<InstanceMetadataProvider>();
        builder.Services.AddSingleton<LeadershipState>();

        var app = builder.Build();
        app.UseInternalListenerIsolation();
        app.UseWebSockets();
        app.MapGatewayHub();
        app.MapInternalPublish();
        app.MapInternalPresence();
        app.MapInternalLeader();

        await app.StartAsync();

        var state = app.Services.GetRequiredService<LeadershipState>();
        return new Gateway(app, publicPort, internalPort, state);
    }

    private static async Task<HttpResponseMessage> GetLeaderAsync(Gateway gateway, string? token, int? port = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"http://127.0.0.1:{port ?? gateway.InternalPort}/internal/leader");
        if (token is not null)
        {
            request.Headers.Add(RealtimePublishToken.Header, token);
        }

        return await http.SendAsync(request);
    }

    [Fact]
    public async Task Leader_WithValidServiceToken_ReturnsLeaderSnapshotShape()
    {
        // A prior reconcile loop has resolved this instance as leader. Any registered
        // service's token — svc-a here — authorizes the read; the response has the
        // exact contract shape (instanceId, isLeader, leaderInstanceId, evaluatedAt).
        await using var gateway = await StartGatewayAsync();
        var evaluatedAt = new DateTimeOffset(2026, 8, 29, 17, 0, 0, TimeSpan.Zero);
        gateway.State.Update(isLeader: true, leaderInstanceId: ThisInstanceId, evaluatedAt: evaluatedAt);

        var response = await GetLeaderAsync(gateway, TokenA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(ThisInstanceId, root.GetProperty("instanceId").GetString());
        Assert.True(root.GetProperty("isLeader").GetBoolean());
        Assert.Equal(ThisInstanceId, root.GetProperty("leaderInstanceId").GetString());
        Assert.Equal(evaluatedAt, root.GetProperty("evaluatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Leader_WithAnyRegisteredServiceToken_IsAuthorized()
    {
        // Unlike /internal/publish (which requires the token to match the CHANNEL's
        // owner), /internal/leader accepts ANY registered service's token — the
        // presenter is only proving it's a container this gateway manages.
        await using var gateway = await StartGatewayAsync();
        gateway.State.Update(isLeader: true, leaderInstanceId: ThisInstanceId, evaluatedAt: DateTimeOffset.UtcNow);

        var aResp = await GetLeaderAsync(gateway, TokenA);
        var bResp = await GetLeaderAsync(gateway, TokenB);

        Assert.Equal(HttpStatusCode.OK, aResp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bResp.StatusCode);
    }

    [Fact]
    public async Task Leader_MissingToken_IsForbidden_NamingTheHeader()
    {
        await using var gateway = await StartGatewayAsync();
        var response = await GetLeaderAsync(gateway, token: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(RealtimePublishToken.Header, body);
    }

    [Fact]
    public async Task Leader_EmptyToken_IsForbidden()
    {
        await using var gateway = await StartGatewayAsync();
        var response = await GetLeaderAsync(gateway, token: "");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Leader_UnknownToken_IsForbidden()
    {
        await using var gateway = await StartGatewayAsync();
        var response = await GetLeaderAsync(gateway, token: "not-any-service-token");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Leader_ManagementAuth_IsNotAccepted_OnThisSurface()
    {
        // The management (Cognito / mgmt client-credentials) path is deliberately NOT
        // wired to this endpoint. Presenting a Bearer token that would satisfy /mgmt
        // must not open the leader endpoint — this surface is for containers only.
        await using var gateway = await StartGatewayAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"http://127.0.0.1:{gateway.InternalPort}/internal/leader");
        request.Headers.Add("Authorization", "Bearer fake-mgmt-token");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Leader_BeforeFirstEvaluation_ReportsNeverEvaluated()
    {
        // Acceptance criterion #645: before the first reconcile evaluation the endpoint
        // reports isLeader=false with null leaderInstanceId and evaluatedAt, so a
        // booting instance never claims leadership through this endpoint.
        await using var gateway = await StartGatewayAsync();

        var response = await GetLeaderAsync(gateway, TokenA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(ThisInstanceId, root.GetProperty("instanceId").GetString());
        Assert.False(root.GetProperty("isLeader").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("leaderInstanceId").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("evaluatedAt").ValueKind);
    }

    [Fact]
    public async Task Leader_FollowerReportsOtherInstanceAsLeader()
    {
        // Two-instance mental model: this box is a follower and knows another instance
        // is the leader. isLeader=false but leaderInstanceId names the peer.
        await using var gateway = await StartGatewayAsync();
        gateway.State.Update(isLeader: false, leaderInstanceId: "i-peer", evaluatedAt: DateTimeOffset.UtcNow);

        var response = await GetLeaderAsync(gateway, TokenA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("isLeader").GetBoolean());
        Assert.Equal("i-peer", doc.RootElement.GetProperty("leaderInstanceId").GetString());
    }

    [Fact]
    public async Task Leader_NotReachable_ViaPublicPort()
    {
        // Acceptance criterion #644: the isolation middleware treats /internal/leader
        // exactly like /internal/publish and /internal/presence — a call on the public
        // listener is 404, regardless of the token.
        await using var gateway = await StartGatewayAsync();

        var response = await GetLeaderAsync(gateway, TokenA, port: gateway.PublicPort);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Leader_NeverInvokesElection_ReadsOnlyCachedState()
    {
        // Acceptance criterion #646: the endpoint performs no database access and
        // never invokes TryAcquireAsync itself. Register a counting election so ANY
        // call to it would be observable, then hit the endpoint repeatedly — the
        // counter must remain at zero because MapInternalLeader consults only
        // LeadershipState, never the election.
        var publicPort = GetFreePort();
        var internalPort = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{publicPort}",
            [InternalListenerOptions.BindEnvVar] = $"127.0.0.1:{internalPort}",
        });

        builder.AddGatewayInternalListener();
        builder.Services.AddGatewayRealtime(builder.Configuration);
        builder.Services.AddSingleton<IManifestStore>(new InMemoryManifestStore(new[]
        {
            Owned("svc-a", TokenA),
        }));
        builder.Services.AddSingleton<IInstanceMetadata>(
            new StubInstanceMetadata(new InstanceIdentity(ThisInstanceId, null, null)));
        builder.Services.AddSingleton<InstanceMetadataProvider>();
        builder.Services.AddSingleton<LeadershipState>();

        // The counting election proves the endpoint never triggers an election.
        var counting = new CountingElection();
        builder.Services.AddSingleton<ILeaderElection>(counting);

        var app = builder.Build();
        app.UseInternalListenerIsolation();
        app.UseWebSockets();
        app.MapInternalLeader();
        await app.StartAsync();

        try
        {
            using var http = new HttpClient();
            for (var i = 0; i < 5; i++)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"http://127.0.0.1:{internalPort}/internal/leader");
                request.Headers.Add(RealtimePublishToken.Header, TokenA);
                var response = await http.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // The endpoint never asks the election anything.
            Assert.Equal(0, counting.Calls);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Election that counts every <see cref="TryAcquireAsync"/> call — used to prove
    /// that the leader endpoint reads only cached state and never triggers an election.
    /// </summary>
    private sealed class CountingElection : ILeaderElection
    {
        public int Calls;

        public Task<LeaderResolution> TryAcquireAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new LeaderResolution(false, null));
        }
    }
}
