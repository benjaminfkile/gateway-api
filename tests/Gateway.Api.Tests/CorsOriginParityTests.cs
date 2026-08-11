using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Tests;

/// <summary>
/// Origin parity between the two CORS surfaces (task #607, finding 3). GATEWAY_CORS_ORIGINS
/// used to reach /mgmt through a verbatim Split() but /hub through a canonicalizing parse,
/// so the same env value could pass one surface and fail the other — a trailing slash or an
/// explicit :443 worked on /hub but not /mgmt — and a malformed entry vanished from /hub with
/// no diagnostic. Now one canonicalization feeds both, and every normalized/dropped entry is
/// logged. These prove a trailing-slash and a default-port origin match BOTH surfaces, and a
/// garbage entry is excluded from both and logged.
/// </summary>
public class CorsOriginParityTests
{
    // Trailing slash, explicit default port, and a garbage (path-bearing) entry.
    private const string TrailingSlash = "https://ops.example.com/";
    private const string TrailingSlashOrigin = "https://ops.example.com";
    private const string DefaultPort = "https://api.example.com:443";
    private const string DefaultPortOrigin = "https://api.example.com";
    private const string Garbage = "http://garbage.example.com/dashboard";
    private const string GarbageOrigin = "http://garbage.example.com";

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Warnings);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _warnings;
            public CapturingLogger(ConcurrentQueue<string> warnings) => _warnings = warnings;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    _warnings.Enqueue(formatter(state, exception));
                }
            }
        }
    }

    private sealed class ParityFactory : WebApplicationFactory<Program>
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("GATEWAY_CORS_ORIGINS", $"{TrailingSlash},{DefaultPort},{Garbage}");
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        }
    }

    private static HttpRequestMessage Preflight(string path, string origin, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        return request;
    }

    [Theory]
    [InlineData("/mgmt/services", "GET")]
    [InlineData("/hub/negotiate", "POST")]
    public async Task TrailingSlashOrigin_MatchesBothSurfaces(string path, string method)
    {
        await using var factory = new ParityFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(path, TrailingSlashOrigin, method));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(TrailingSlashOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Theory]
    [InlineData("/mgmt/services", "GET")]
    [InlineData("/hub/negotiate", "POST")]
    public async Task DefaultPortOrigin_MatchesBothSurfaces(string path, string method)
    {
        await using var factory = new ParityFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(path, DefaultPortOrigin, method));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(DefaultPortOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Theory]
    [InlineData("/mgmt/services", "GET")]
    [InlineData("/hub/negotiate", "POST")]
    public async Task GarbageEntry_ExcludedFromBothSurfaces(string path, string method)
    {
        await using var factory = new ParityFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(path, GarbageOrigin, method));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task NormalizedAndDroppedEntries_AreLoggedAtStartup()
    {
        await using var factory = new ParityFactory();
        // Force the host to build (and thus run the startup logging).
        using var client = factory.CreateClient();

        var warnings = factory.Logs.Warnings.ToArray();
        Assert.Contains(warnings, w => w.Contains(TrailingSlash) && w.Contains(TrailingSlashOrigin) && w.Contains("normalized"));
        Assert.Contains(warnings, w => w.Contains(DefaultPort) && w.Contains(DefaultPortOrigin) && w.Contains("normalized"));
        Assert.Contains(warnings, w => w.Contains(Garbage) && w.Contains("dropped"));
    }
}
