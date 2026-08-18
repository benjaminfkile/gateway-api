using System.Diagnostics;
using Gateway.Api.Data;
using Gateway.Api.Health;
using Gateway.Api.Proxy;
using Gateway.Api.Reconcile;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Tests;

/// <summary>
/// Direct tests of the production <see cref="HttpHealthProber"/> against a real
/// localhost server, covering the success/failure/timeout mapping and — via a
/// slow server — the 3s per-probe deadline.
/// </summary>
public class HttpHealthProberTests
{
    /// <summary>A localhost server whose /api/health behaviour the test controls.</summary>
    private sealed class ProbeServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public int Port { get; }

        private ProbeServer(WebApplication app, int port)
        {
            _app = app;
            Port = port;
        }

        public static async Task<ProbeServer> StartAsync(int statusCode, TimeSpan delay)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            app.MapGet("/api/health", async (HttpContext ctx) =>
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ctx.RequestAborted);
                }

                ctx.Response.StatusCode = statusCode;
                await ctx.Response.WriteAsync("probe");
            });

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            return new ProbeServer(app, new Uri(address).Port);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Points the prober at a localhost port instead of container DNS.</summary>
    private sealed class LoopbackAddressResolver : IServiceAddressResolver
    {
        public string Resolve(ServiceManifest manifest) => $"http://127.0.0.1:{manifest.Port}";
    }

    private static (HttpHealthProber Prober, ServiceProvider Sp) BuildProber()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HttpHealthProber.HttpClientName, c => c.Timeout = HttpHealthProber.ProbeTimeout);
        var sp = services.BuildServiceProvider();
        var prober = new HttpHealthProber(
            sp.GetRequiredService<IHttpClientFactory>(),
            new LoopbackAddressResolver());
        return (prober, sp);
    }

    private static ServiceManifest Manifest(int port) => new()
    {
        Name = "svc-a",
        Image = "registry/svc-a",
        Tag = "latest",
        Port = port,
        DesiredStatus = "running",
        IncludeInHealth = true,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Probe_Returns_Up_For2xx()
    {
        await using var server = await ProbeServer.StartAsync(200, TimeSpan.Zero);
        var (prober, sp) = BuildProber();
        await using var _ = sp;

        var result = await prober.ProbeAsync(Manifest(server.Port));

        Assert.Equal("up", result.Status);
        Assert.Equal(200, result.HttpStatus);
        Assert.NotNull(result.ResponseTimeMs);
        Assert.True(result.ResponseTimeMs >= 0);
    }

    [Fact]
    public async Task Probe_Returns_Down_ForNon2xx_KeepingStatus()
    {
        await using var server = await ProbeServer.StartAsync(503, TimeSpan.Zero);
        var (prober, sp) = BuildProber();
        await using var _ = sp;

        var result = await prober.ProbeAsync(Manifest(server.Port));

        Assert.Equal("down", result.Status);
        Assert.Equal(503, result.HttpStatus);
        Assert.NotNull(result.ResponseTimeMs);
    }

    [Fact]
    public async Task Probe_TimesOut_WithinDeadline_ReportedAsDownWithNulls()
    {
        // Server answers well after the 3s probe deadline.
        await using var server = await ProbeServer.StartAsync(200, TimeSpan.FromSeconds(10));
        var (prober, sp) = BuildProber();
        await using var _ = sp;

        var sw = Stopwatch.StartNew();
        var result = await prober.ProbeAsync(Manifest(server.Port));
        sw.Stop();

        Assert.Equal("down", result.Status);
        Assert.Null(result.HttpStatus);
        Assert.Null(result.ResponseTimeMs);
        // Must give up around the 3s bound, not wait for the 10s server.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6),
            $"probe should have timed out near 3s but took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task Probe_ConnectionRefused_ReportedAsDownWithNulls()
    {
        var (prober, sp) = BuildProber();
        await using var _ = sp;

        // Nothing is listening on this port.
        var result = await prober.ProbeAsync(Manifest(59_999));

        Assert.Equal("down", result.Status);
        Assert.Null(result.HttpStatus);
        Assert.Null(result.ResponseTimeMs);
    }

    [Fact]
    public async Task ReconcilerMode_NoHostPort_ReturnsUnreachable_WithoutTouchingManifestPort()
    {
        // task #98 / incident 2026-08-17: in reconciler mode a missing
        // canonical-container port must SHORT-CIRCUIT — no HTTP call, no
        // connection attempt. The manifest port is a container-internal port
        // whose host counterpart may belong to a completely different service.
        var services = new ServiceCollection();
        services.AddHttpClient(HttpHealthProber.HttpClientName, c => c.Timeout = HttpHealthProber.ProbeTimeout);
        await using var sp = services.BuildServiceProvider();

        // Start a server on the "manifest port" that would answer 200 if probed —
        // the prober must NOT hit it. Assert on the request count for confidence.
        var hits = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var stranger = builder.Build();
        stranger.MapGet("/api/health", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref hits);
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        await stranger.StartAsync();
        var strangerPort = new Uri(stranger.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

        try
        {
            var prober = new HttpHealthProber(
                sp.GetRequiredService<IHttpClientFactory>(),
                new LoopbackAddressResolver(),
                hostPorts: new ServiceHostPortMap(), // empty — no canonical port
                reconcilerOptions: new ReconcilerOptions { Enabled = true });

            var result = await prober.ProbeAsync(Manifest(strangerPort));

            Assert.Equal("down", result.Status);
            Assert.Null(result.HttpStatus);
            Assert.Null(result.ResponseTimeMs);
            Assert.Equal(0, hits); // the stranger on the manifest port was NEVER hit
        }
        finally
        {
            await stranger.StopAsync();
            await stranger.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReconcilerMode_HostPortRecorded_ProbesRealPort()
    {
        // Complement: once the reconciler records a canonical container port, the
        // prober targets it (not the manifest port).
        await using var server = await ProbeServer.StartAsync(200, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddHttpClient(HttpHealthProber.HttpClientName, c => c.Timeout = HttpHealthProber.ProbeTimeout);
        await using var sp = services.BuildServiceProvider();

        var map = new ServiceHostPortMap();
        map.Set("svc-a", server.Port);
        var prober = new HttpHealthProber(
            sp.GetRequiredService<IHttpClientFactory>(),
            new LoopbackAddressResolver(),
            hostPorts: map,
            reconcilerOptions: new ReconcilerOptions { Enabled = true });

        // Manifest port is intentionally garbage — the map entry must win.
        var result = await prober.ProbeAsync(Manifest(1));

        Assert.Equal("up", result.Status);
        Assert.Equal(200, result.HttpStatus);
    }

    [Fact]
    public async Task ProxyOnlyMode_NoHostPort_FallsBackToManifestPort()
    {
        // Requirement: with the reconciler DISABLED (proxy-only dev mode per the
        // README), the manifest-port fallback is preserved — the developer's
        // local process listens on it.
        await using var server = await ProbeServer.StartAsync(200, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddHttpClient(HttpHealthProber.HttpClientName, c => c.Timeout = HttpHealthProber.ProbeTimeout);
        await using var sp = services.BuildServiceProvider();

        var prober = new HttpHealthProber(
            sp.GetRequiredService<IHttpClientFactory>(),
            new LoopbackAddressResolver(),
            hostPorts: new ServiceHostPortMap(),
            reconcilerOptions: new ReconcilerOptions { Enabled = false });

        var result = await prober.ProbeAsync(Manifest(server.Port));

        Assert.Equal("up", result.Status);
        Assert.Equal(200, result.HttpStatus);
    }
}
