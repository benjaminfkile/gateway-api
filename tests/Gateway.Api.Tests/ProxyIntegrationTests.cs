using System.Net;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.Reconcile;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration tests for the manifest-driven YARP proxy. The gateway runs under
/// <see cref="WebApplicationFactory{TEntryPoint}"/> with an in-memory manifest
/// store; routes are forwarded to a real Kestrel downstream server on localhost.
/// </summary>
public class ProxyIntegrationTests
{
    /// <summary>Points routes at the localhost test server rather than container DNS.</summary>
    private sealed class LoopbackAddressResolver : IServiceAddressResolver
    {
        public string Resolve(ServiceManifest manifest) => $"http://127.0.0.1:{manifest.Port}";
    }

    private sealed class ProxyTestFactory : WebApplicationFactory<Program>
    {
        // Captured so tests can mutate the manifest at runtime and refresh routes.
        public InMemoryManifestStore Store { get; } = new();

        /// <summary>
        /// When set, ProxyStateService sees a reconciler-enabled ReconcilerOptions —
        /// so its manifest-port fallback is suppressed and a service with no
        /// canonical container port serves 503 (task #98, incident 2026-08-17).
        /// </summary>
        public bool ReconcilerEnabled { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                services.AddSingleton<IManifestStore>(Store);

                services.RemoveAll<IServiceAddressResolver>();
                services.AddSingleton<IServiceAddressResolver, LoopbackAddressResolver>();

                // Deterministic hardening (task #111): freeze the clock
                // ProxyStateService reads for swap-start stamps (task #99). No
                // test in this file asserts on the swap-age math, but the
                // ambient TimeProvider.System stamp adds a real-clock read to
                // every SwapDestinationAsync call — a race magnet under CI CPU
                // contention. UnixEpoch keeps the stamp stable and reproducible.
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new ManualTimeProvider());

                // Deterministic hardening (task #111): the ReconcilerService
                // background loop starts as a hosted service whenever the
                // gateway boots — regardless of ReconcilerOptions.Enabled it
                // sits on the thread pool competing for the same slots as the
                // test assertions. With Enabled=true it races even harder
                // (spins into its first RunOnceAsync immediately, which then
                // throws on UnavailableContainerRuntime and quietly sleeps for
                // the Interval). Nothing in this file asserts reconciler
                // convergence — routes/health are driven directly — so remove
                // the loop entirely.
                RemoveHostedService<ReconcilerService>(services);

                if (ReconcilerEnabled)
                {
                    services.RemoveAll<ReconcilerOptions>();
                    services.AddSingleton(new ReconcilerOptions { Enabled = true });
                }
            });
        }

        private static void RemoveHostedService<T>(IServiceCollection services)
            where T : IHostedService
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                var d = services[i];
                if (d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T))
                {
                    services.RemoveAt(i);
                }
            }
        }

        public Task RefreshRoutesAsync() =>
            Services.GetRequiredService<ProxyStateService>().RefreshRoutesAsync();

        public ServiceHostPortMap HostPorts =>
            Services.GetRequiredService<ServiceHostPortMap>();

        public ProxyStateService ProxyState =>
            Services.GetRequiredService<ProxyStateService>();
    }

    private static ServiceManifest RunningManifest(string name, int port) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Port = port,
        DesiredStatus = "running",
        IncludeInHealth = true,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task ProxiesRequest_PreservingRemainderOfPathAndQuery()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-a/orders/42?status=open&page=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The '/svc-a' prefix is stripped; the app sees only its own path + query.
        Assert.Equal("/orders/42?status=open&page=2", body);
    }

    [Fact]
    public async Task ProxiesRootOfService()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-a/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NonManifestPath_Returns404()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-b/anything");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StoppedService_HasNoRoute_Returns404()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        var stopped = RunningManifest("svc-a", downstream.Port);
        stopped.DesiredStatus = "stopped";
        await factory.Store.UpsertAsync(stopped);

        var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-a/orders");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ManifestChange_TakesEffect_AfterRefresh_WithoutRestart()
    {
        await using var first = await DownstreamTestServer.StartAsync();
        await using var second = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", first.Port));

        var client = factory.CreateClient();

        // svc-b is not in the manifest yet → 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/svc-b/ping")).StatusCode);

        // Add svc-b and refresh routes on the *same* running gateway.
        await factory.Store.UpsertAsync(RunningManifest("svc-b", second.Port));
        await factory.RefreshRoutesAsync();

        // svc-a still works, svc-b now routes — no restart.
        var aResponse = await client.GetAsync("/svc-a/ping");
        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        Assert.Equal("/ping", await aResponse.Content.ReadAsStringAsync());

        var bResponse = await client.GetAsync("/svc-b/ping");
        Assert.Equal(HttpStatusCode.OK, bResponse.StatusCode);
        Assert.Equal("/ping", await bResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SetDesiredStatusStopped_ThenRefresh_RemovesRoute()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/svc-a/ping")).StatusCode);

        await factory.Store.SetDesiredStatusAsync("svc-a", "stopped");
        await factory.RefreshRoutesAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/svc-a/ping")).StatusCode);
    }

    [Fact]
    public async Task ReconcilerMode_NoContainerPort_Serves503_ForRunningManifest()
    {
        // task #98 / incident 2026-08-17: in reconciler mode a service desired
        // running whose canonical container port is unknown must NOT fall back to
        // the manifest port on the host — that host port is a Docker-assigned
        // ephemeral that either has nothing listening or belongs to a stranger, so
        // silently proxying to it can serve one service's traffic from another
        // service's process (the incident: prod svc-a landing on its -dev sibling
        // that shares the same container-side port). The route must serve 503.
        await using var factory = new ProxyTestFactory { ReconcilerEnabled = true };
        await factory.Store.UpsertAsync(RunningManifest("svc-a", 8080));

        var client = factory.CreateClient();
        var response = await client.GetAsync("/svc-a/orders");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReconcilerMode_ContainerPortRecorded_ForwardsToRealPort()
    {
        // Complement of the previous test: once the reconciler has recorded the
        // canonical container's host port, the route forwards there normally — not
        // to the manifest port. The forwarded path is preserved as usual.
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory { ReconcilerEnabled = true };
        // Manifest port is a fake container-internal port; the real host port is
        // the downstream test server's dynamic port, published via the host-port
        // map (what the reconciler would do on a real box).
        await factory.Store.UpsertAsync(RunningManifest("svc-a", 8080));
        var client = factory.CreateClient();

        // Before the map has the port, the route is 503.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/svc-a/ping")).StatusCode);

        factory.HostPorts.Set("svc-a", downstream.Port);
        await factory.RefreshRoutesAsync();

        var response = await client.GetAsync("/svc-a/orders?id=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/orders?id=1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReconcilerMode_SwapOverride_TrumpsMissingContainerPort()
    {
        // Requirement #3: a blue-green destination override is unaffected by the
        // fallback change. Even if the canonical container port is not recorded
        // (e.g. the very first swap on a service the reconciler has not yet
        // primed), the override still routes traffic to the green candidate.
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory { ReconcilerEnabled = true };
        await factory.Store.UpsertAsync(RunningManifest("svc-a", 8080));

        // No host-port map entry → without an override this would be a 503 (asserted above).
        await factory.ProxyState.SwapDestinationAsync("svc-a", $"http://127.0.0.1:{downstream.Port}");

        var client = factory.CreateClient();
        var response = await client.GetAsync("/svc-a/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/ping", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProxyOnlyMode_ManifestPortFallback_StillWorks()
    {
        // Requirement: with the reconciler DISABLED (proxy-only dev mode per the
        // README), the manifest port IS the host port the developer is running the
        // app on locally — the fallback must be preserved. This is the default
        // factory (ReconcilerEnabled = false) forwarding to a real localhost port.
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory(); // reconciler disabled
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();
        var response = await client.GetAsync("/svc-a/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/ping", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProxiedRoute_HasNoAuthChallenge()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        // No Authorization header supplied. A gateway that authenticated app
        // traffic would answer 401; the design invariant forbids that.
        var response = await client.GetAsync("/svc-a/secret");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
