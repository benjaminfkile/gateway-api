using System.Net;
using Gateway.Api.Instances;

namespace Gateway.Api.Tests;

/// <summary>
/// Tests instance-identity resolution (tech-spec §4.4): IMDSv2 reads via a fake
/// HTTP handler (no network), the environment fallback, and the provider's
/// auto-selection — IMDS when reachable, environment when not.
/// </summary>
public class InstanceMetadataTests
{
    /// <summary>Scripts IMDS-style HTTP responses by method + path, or throws to simulate unreachable.</summary>
    private sealed class FakeImdsHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage?> _respond;

        public FakeImdsHandler(Func<HttpRequestMessage, HttpResponseMessage?> respond)
        {
            _respond = respond;
        }

        public List<string> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var response = _respond(request);
            if (response is null)
            {
                throw new HttpRequestException("simulated unreachable IMDS");
            }

            return Task.FromResult(response);
        }
    }

    private static HttpClient ImdsClient(FakeImdsHandler handler) =>
        new(handler) { BaseAddress = new Uri(Ec2InstanceMetadata.ImdsBaseAddress) };

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Ec2_ResolvesIdentity_ViaImdsV2TokenThenMetadata()
    {
        var handler = new FakeImdsHandler(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith("/api/token"))
            {
                Assert.True(req.Headers.Contains("X-aws-ec2-metadata-token-ttl-seconds"));
                return Ok("the-token");
            }

            // Metadata GETs must carry the token issued above.
            Assert.True(req.Headers.Contains("X-aws-ec2-metadata-token"));
            return req.RequestUri!.AbsolutePath switch
            {
                "/latest/meta-data/instance-id" => Ok("i-0abc123"),
                "/latest/meta-data/local-ipv4" => Ok("10.1.2.3"),
                "/latest/meta-data/public-ipv4" => Ok("54.1.2.3"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var ec2 = new Ec2InstanceMetadata(ImdsClient(handler));
        var identity = await ec2.TryResolveAsync();

        Assert.NotNull(identity);
        Assert.Equal("i-0abc123", identity!.InstanceId);
        Assert.Equal("10.1.2.3", identity.PrivateIp);
        Assert.Equal("54.1.2.3", identity.PublicIp);
    }

    [Fact]
    public async Task Ec2_ReturnsNull_WhenImdsUnreachable()
    {
        // Every call throws — the off-EC2 case.
        var handler = new FakeImdsHandler(_ => null);
        var ec2 = new Ec2InstanceMetadata(ImdsClient(handler), TimeSpan.FromMilliseconds(50));

        Assert.Null(await ec2.TryResolveAsync());
    }

    [Fact]
    public async Task Env_ReadsConfiguredVars()
    {
        var vars = new Dictionary<string, string?>
        {
            [EnvInstanceMetadata.InstanceIdVar] = "gw-01",
            [EnvInstanceMetadata.PrivateIpVar] = "10.9.9.9",
            [EnvInstanceMetadata.PublicIpVar] = "  ", // whitespace → treated as absent
        };
        var env = new EnvInstanceMetadata(k => vars.GetValueOrDefault(k), () => "machine-x");

        var identity = await env.TryResolveAsync();

        Assert.NotNull(identity);
        Assert.Equal("gw-01", identity!.InstanceId);
        Assert.Equal("10.9.9.9", identity.PrivateIp);
        Assert.Null(identity.PublicIp);
    }

    [Fact]
    public async Task Env_FallsBackToMachineName_WhenInstanceIdUnset()
    {
        var env = new EnvInstanceMetadata(_ => null, () => "machine-x");

        var identity = await env.TryResolveAsync();

        Assert.Equal("machine-x", identity!.InstanceId);
        Assert.Null(identity.PrivateIp);
    }

    [Fact]
    public async Task Provider_SelectsEnv_WhenImdsUnreachable()
    {
        // Ec2 source declines (IMDS unreachable) → env fallback auto-selected.
        var provider = new InstanceMetadataProvider(new IInstanceMetadata[]
        {
            new StubInstanceMetadata(null),
            new StubInstanceMetadata(new InstanceIdentity("env-id", "10.0.0.1", null)),
        });

        var identity = await provider.GetAsync();

        Assert.Equal("env-id", identity.InstanceId);
    }

    [Fact]
    public async Task Provider_PrefersImds_WhenAvailable()
    {
        var provider = new InstanceMetadataProvider(new IInstanceMetadata[]
        {
            new StubInstanceMetadata(new InstanceIdentity("imds-id", "10.0.0.2", "1.2.3.4")),
            new StubInstanceMetadata(new InstanceIdentity("env-id", "10.0.0.1", null)),
        });

        var identity = await provider.GetAsync();

        Assert.Equal("imds-id", identity.InstanceId);
        Assert.Equal("1.2.3.4", identity.PublicIp);
    }

    [Fact]
    public async Task Provider_CachesResolvedIdentity()
    {
        var provider = new InstanceMetadataProvider(new IInstanceMetadata[]
        {
            new StubInstanceMetadata(new InstanceIdentity("cached-id", null, null)),
        });

        var first = await provider.GetAsync();
        var second = await provider.GetAsync();

        Assert.Same(first, second);
    }
}
