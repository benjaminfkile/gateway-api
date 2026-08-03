using System.Diagnostics;
using Gateway.Api.Data;
using Gateway.Api.Health;
using Gateway.Api.Proxy;
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
}
