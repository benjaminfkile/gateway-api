using Gateway.Api.RealTime;
using Microsoft.Extensions.Configuration;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for the environment-driven real-time options: the Redis
/// backplane is off unless an endpoint is set (tech-spec §4.2), and the internal
/// listener parses <c>host:port</c> for the isolation discriminator.
/// </summary>
public class RealtimeOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void Redis_Disabled_WhenEndpointUnset()
    {
        var options = RedisBackplaneOptions.FromConfiguration(Config());

        Assert.False(options.Enabled);
        Assert.Null(options.Endpoint);
    }

    [Fact]
    public void Redis_Enabled_WhenEndpointSet_SslDefaultsTrue()
    {
        var options = RedisBackplaneOptions.FromConfiguration(
            Config((RedisBackplaneOptions.EndpointEnvVar, "redis.internal:6379")));

        Assert.True(options.Enabled);
        Assert.Equal("redis.internal:6379", options.Endpoint);
        Assert.True(options.UseSsl);
    }

    [Fact]
    public void Redis_SslDisabled_ByExplicitFalse()
    {
        var options = RedisBackplaneOptions.FromConfiguration(Config(
            (RedisBackplaneOptions.EndpointEnvVar, "redis.internal:6379"),
            (RedisBackplaneOptions.SslEnvVar, "false")));

        Assert.True(options.Enabled);
        Assert.False(options.UseSsl);
    }

    [Fact]
    public void InternalListener_DefaultsToPort8080()
    {
        var options = InternalListenerOptions.FromConfiguration(Config());

        Assert.Equal(InternalListenerOptions.DefaultBind, options.Bind);
        Assert.Equal(8080, options.Port);
        Assert.Equal("http://0.0.0.0:8080", options.Url);
    }

    [Fact]
    public void InternalListener_ParsesHostAndPort()
    {
        var options = InternalListenerOptions.FromConfiguration(
            Config((InternalListenerOptions.BindEnvVar, "127.0.0.1:9099")));

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(9099, options.Port);
    }

    [Fact]
    public void InternalListener_RejectsMalformedBind()
    {
        Assert.Throws<InvalidOperationException>(() =>
            InternalListenerOptions.FromConfiguration(
                Config((InternalListenerOptions.BindEnvVar, "not-a-bind"))));
    }
}
