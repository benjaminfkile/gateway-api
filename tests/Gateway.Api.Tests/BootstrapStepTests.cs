using Gateway.Api.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Tests the node-bootstrap steps (tech-spec §4.3) against a <see cref="FakeLinuxHost"/>
/// — no real filesystem, Docker, or AWS. The core property is <b>idempotency</b>:
/// each step applies changes on the first run and reports "no change" on the second,
/// and each reports what it changed vs. skipped. Also covers the whole pipeline and
/// the run-once env-flag gate on the hosted service.
/// </summary>
public class BootstrapStepTests
{
    private static BootstrapOptions Options() => new();

    // ---- Docker daemon log-rotation config -------------------------------------

    [Fact]
    public async Task DaemonConfig_writes_and_reloads_on_first_run_then_is_idempotent()
    {
        var host = new FakeLinuxHost();
        var options = Options();
        var step = new DockerDaemonConfigStep(host, options, NullLogger<DockerDaemonConfigStep>.Instance);

        var first = await step.RunAsync();

        Assert.True(first.Changed);
        Assert.True(host.Files.ContainsKey(options.DockerDaemonConfigPath));
        var written = host.Files[options.DockerDaemonConfigPath];
        Assert.Contains("\"max-size\": \"10m\"", written);
        Assert.Contains("\"max-file\": \"3\"", written);
        Assert.Single(host.Commands, c => c.Executable == "systemctl");

        var second = await step.RunAsync();

        Assert.False(second.Changed);
        // No second write and no second reload.
        Assert.Single(host.Commands, c => c.Executable == "systemctl");
        Assert.Equal(written, host.Files[options.DockerDaemonConfigPath]);
    }

    [Fact]
    public async Task DaemonConfig_preserves_unrelated_existing_keys()
    {
        var host = new FakeLinuxHost();
        var options = Options();
        host.Files[options.DockerDaemonConfigPath] =
            "{ \"live-restore\": true }";

        var step = new DockerDaemonConfigStep(host, options, NullLogger<DockerDaemonConfigStep>.Instance);

        var result = await step.RunAsync();

        Assert.True(result.Changed);
        var written = host.Files[options.DockerDaemonConfigPath];
        Assert.Contains("live-restore", written);
        Assert.Contains("log-driver", written);

        // Converged now: a second run changes nothing.
        Assert.False((await step.RunAsync()).Changed);
    }

    // ---- Internal Docker network -----------------------------------------------

    [Fact]
    public async Task Network_created_once_then_idempotent()
    {
        var host = new FakeLinuxHost();
        var options = Options();
        var step = new DockerNetworkStep(host, options, NullLogger<DockerNetworkStep>.Instance);

        var first = await step.RunAsync();

        Assert.True(first.Changed);
        Assert.Contains(options.Network, host.Networks);

        var second = await step.RunAsync();

        Assert.False(second.Changed);
        Assert.Single(host.Commands, c => c.Executable == "docker" && c.Arguments.Contains("create"));
    }

    [Fact]
    public async Task Network_create_failure_throws()
    {
        var host = new FakeLinuxHost
        {
            // Force both inspect and create to fail.
            Handler = (_, _, _) => new ProcessResult(1, string.Empty, "boom"),
        };
        var step = new DockerNetworkStep(host, Options(), NullLogger<DockerNetworkStep>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => step.RunAsync());
    }

    // ---- Registry login --------------------------------------------------------

    [Fact]
    public async Task RegistryLogin_logs_in_once_then_idempotent_and_re_logs_on_rotation()
    {
        var host = new FakeLinuxHost();
        var registry = new FakeImageRegistry();
        var options = Options();
        var step = new RegistryLoginStep(host, registry, options, NullLogger<RegistryLoginStep>.Instance);

        var first = await step.RunAsync();

        Assert.True(first.Changed);
        var login = Assert.Single(host.Commands, c => c.Executable == "docker" && c.Arguments[0] == "login");
        // Password goes over stdin, never in the argument list.
        Assert.Equal(registry.Credentials.Password, login.StandardInput);
        Assert.DoesNotContain(registry.Credentials.Password, login.Arguments);
        Assert.Contains("--password-stdin", login.Arguments);
        // Only a fingerprint is persisted — not the secret.
        var marker = host.Files[options.RegistryAuthStatePath];
        Assert.DoesNotContain(registry.Credentials.Password, marker);

        var second = await step.RunAsync();

        Assert.False(second.Changed);
        Assert.Single(host.Commands, c => c.Executable == "docker" && c.Arguments[0] == "login");

        // Credentials rotate → re-login.
        registry.Credentials = registry.Credentials with { Password = "rotated-token" };
        var third = await step.RunAsync();

        Assert.True(third.Changed);
        Assert.Equal(2, host.Commands.Count(c => c.Executable == "docker" && c.Arguments[0] == "login"));
    }

    [Fact]
    public async Task RegistryLogin_skips_when_no_credentials_available()
    {
        var host = new FakeLinuxHost();
        var registry = new FakeImageRegistry { CredentialsError = new InvalidOperationException("no region") };
        var step = new RegistryLoginStep(host, registry, Options(), NullLogger<RegistryLoginStep>.Instance);

        var result = await step.RunAsync();

        Assert.False(result.Changed);
        Assert.DoesNotContain(host.Commands, c => c.Executable == "docker" && c.Arguments[0] == "login");
    }

    // ---- CloudWatch agent config -----------------------------------------------

    [Fact]
    public async Task CloudWatchConfig_writes_and_reloads_on_first_run_then_is_idempotent()
    {
        var host = new FakeLinuxHost();
        var options = Options();
        var step = new CloudWatchAgentConfigStep(host, options, NullLogger<CloudWatchAgentConfigStep>.Instance);

        var first = await step.RunAsync();

        Assert.True(first.Changed);
        Assert.True(host.Files.ContainsKey(options.CloudWatchAgentConfigPath));
        var written = host.Files[options.CloudWatchAgentConfigPath];
        Assert.Contains(options.MetricsNamespace, written);
        Assert.Contains("mem_used_percent", written);
        Assert.Single(host.Commands, c => c.Executable == options.CloudWatchAgentCtlPath);

        var second = await step.RunAsync();

        Assert.False(second.Changed);
        Assert.Single(host.Commands, c => c.Executable == options.CloudWatchAgentCtlPath);
    }

    // ---- Full pipeline ---------------------------------------------------------

    [Fact]
    public async Task Pipeline_runs_all_steps_and_is_idempotent_on_second_run()
    {
        var host = new FakeLinuxHost();
        var registry = new FakeImageRegistry();
        var options = Options();
        var steps = BuildSteps(host, registry, options);
        var bootstrap = new NodeBootstrap(steps, NullLogger<NodeBootstrap>.Instance);

        await bootstrap.RunAsync();

        var mutationsAfterFirst = MutatingCommandCount(host);
        var filesAfterFirst = new Dictionary<string, string>(host.Files, StringComparer.Ordinal);
        Assert.True(mutationsAfterFirst > 0);

        await bootstrap.RunAsync();

        // A converged box performs no further mutations. Read-only probes (e.g.
        // `docker network inspect`) may re-run, but nothing is written or changed.
        Assert.Equal(mutationsAfterFirst, MutatingCommandCount(host));
        Assert.Equal(filesAfterFirst, host.Files);
    }

    /// <summary>Count commands that change host state (writes/logins/reloads), ignoring read-only probes.</summary>
    private static int MutatingCommandCount(FakeLinuxHost host) =>
        host.Commands.Count(c =>
            !(c.Executable == "docker" && c.Arguments.Length >= 2 && c.Arguments[1] == "inspect"));

    [Fact]
    public async Task Pipeline_continues_past_a_failing_step()
    {
        var host = new FakeLinuxHost();
        var registry = new FakeImageRegistry();
        var options = Options();

        // A step that always throws, followed by a real step that must still run.
        var steps = new IBootstrapStep[]
        {
            new ThrowingStep(),
            new DockerNetworkStep(host, options, NullLogger<DockerNetworkStep>.Instance),
        };
        var bootstrap = new NodeBootstrap(steps, NullLogger<NodeBootstrap>.Instance);

        await bootstrap.RunAsync();

        Assert.Contains(options.Network, host.Networks);
    }

    [Fact]
    public async Task Disabled_bootstrap_does_nothing()
    {
        var host = new FakeLinuxHost();
        var options = Options(); // Enabled defaults to false

        // A provider that throws if resolved proves the disabled service never even
        // constructs the pipeline (and thus never touches the box).
        var service = new BootstrapHostedService(
            new ThrowingServiceProvider(), options, NullLogger<BootstrapHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(host.Commands);
        Assert.Empty(host.Files);
    }

    private static IBootstrapStep[] BuildSteps(FakeLinuxHost host, FakeImageRegistry registry, BootstrapOptions options) =>
        new IBootstrapStep[]
        {
            new DockerDaemonConfigStep(host, options, NullLogger<DockerDaemonConfigStep>.Instance),
            new DockerNetworkStep(host, options, NullLogger<DockerNetworkStep>.Instance),
            new RegistryLoginStep(host, registry, options, NullLogger<RegistryLoginStep>.Instance),
            new CloudWatchAgentConfigStep(host, options, NullLogger<CloudWatchAgentConfigStep>.Instance),
        };

    private sealed class ThrowingStep : IBootstrapStep
    {
        public string Name => "throwing";

        public Task<BootstrapStepResult> RunAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("step boom");
    }

    private sealed class ThrowingServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException("pipeline must not be resolved when bootstrap is disabled");
    }
}
