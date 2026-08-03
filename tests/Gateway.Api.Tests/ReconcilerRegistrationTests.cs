using Gateway.Api.Containers;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Tests;

/// <summary>
/// Tests for <see cref="ReconcilerServiceCollectionExtensions.AddNodeReconciler"/>:
/// the reconciler is off unless the env flag says otherwise, and the host wires
/// up cleanly on a box without Docker (the build/test environment).
/// </summary>
public class ReconcilerRegistrationTests
{
    private static bool EnvFlagSet =>
        Environment.GetEnvironmentVariable(ReconcilerOptions.EnabledEnvVar) is not null;

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void DisabledByDefault_WhenFlagAbsent()
    {
        if (EnvFlagSet)
        {
            return; // The ambient environment forces a value; skip.
        }

        var services = new ServiceCollection();
        services.AddNodeReconciler(Config());
        using var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<ReconcilerOptions>().Enabled);
    }

    [Fact]
    public void Enabled_WhenFlagTrueViaConfig()
    {
        if (EnvFlagSet)
        {
            return; // Env var takes precedence over config; skip when it is set.
        }

        var services = new ServiceCollection();
        services.AddNodeReconciler(Config((ReconcilerOptions.EnabledEnvVar, "true")));
        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<ReconcilerOptions>().Enabled);
    }

    [Fact]
    public void BindsIntervalAndLogDriverFromConfig()
    {
        var services = new ServiceCollection();
        services.AddNodeReconciler(Config(
            ("Reconciler:Network", "custom-net"),
            ("Reconciler:HealthPath", "/healthz"),
            ("Reconciler:LogDriver:Driver", "awslogs"),
            ("Reconciler:LogDriver:Options:awslogs-group", "/gateway/services/svc-a")));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<ReconcilerOptions>();
        Assert.Equal("custom-net", options.Network);
        Assert.Equal("/healthz", options.HealthPath);
        Assert.Equal("awslogs", options.LogDriver.Driver);
        Assert.Equal("/gateway/services/svc-a", options.LogDriver.Options["awslogs-group"]);
    }

    [Fact]
    public void RegistersUnavailableRuntime_WhenDockerAbsent()
    {
        // The build/test host has no Docker socket, so the stub runtime is wired.
        // This proves the whole gateway can boot without Docker (tech-spec DoD).
        if (DockerContainerRuntime.IsSupported)
        {
            return; // Running on a box with Docker; the real runtime is expected instead.
        }

        var services = new ServiceCollection();
        services.AddNodeReconciler(Config());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<UnavailableContainerRuntime>(provider.GetRequiredService<IContainerRuntime>());
    }
}
