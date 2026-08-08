using Gateway.Api.Containers;
using Gateway.Api.Reconcile;

namespace Gateway.Api.Tests;

/// <summary>
/// Exhaustive unit tests for the pure <see cref="ReconcilePlanner"/> — every
/// desired × actual combination that maps to Start / StopRemove /
/// BlueGreenReplace / None, plus orphan handling, drift rules, and deterministic
/// ordering. No I/O, no fakes needed: the planner is a pure function.
/// </summary>
public class ReconcilePlannerTests
{
    private static readonly IReadOnlyDictionary<string, string> NoEnv =
        new Dictionary<string, string>();

    // A fixed clock so the restart-drift tests reason about StartedAt vs
    // restart_requested_at without touching the wall clock (the planner is pure).
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static DesiredService Desired(
        string name,
        string status = "running",
        string? digest = "sha256:v1",
        string? envHash = "env-1",
        int port = 8080,
        DateTimeOffset? restartRequestedAt = null) =>
        new(name, $"registry/{name}", "latest", digest, port, status, envHash, NoEnv, restartRequestedAt);

    private static ContainerInfo Container(
        string name,
        string? digest = "sha256:v1",
        string? envHash = "env-1",
        string state = "running",
        DateTimeOffset? startedAt = null) =>
        new(name, $"registry/{name}", digest, state, startedAt ?? DateTimeOffset.UnixEpoch, envHash);

    private static ReconcileAction Single(
        IReadOnlyList<DesiredService> desired,
        IReadOnlyList<ContainerInfo> actual)
    {
        var plan = ReconcilePlanner.Plan(desired, actual);
        return Assert.Single(plan);
    }

    [Fact]
    public void RunningDesired_NoContainer_Starts()
    {
        var action = Single(new[] { Desired("svc-a") }, Array.Empty<ContainerInfo>());

        Assert.Equal(ReconcileActionKind.Start, action.Kind);
        Assert.Equal("svc-a", action.ServiceName);
        Assert.NotNull(action.Desired);
        Assert.Null(action.Actual);
    }

    [Fact]
    public void RunningDesired_MatchingContainer_None()
    {
        var action = Single(
            new[] { Desired("svc-a", digest: "sha256:v1", envHash: "env-1") },
            new[] { Container("svc-a", digest: "sha256:v1", envHash: "env-1") });

        Assert.Equal(ReconcileActionKind.None, action.Kind);
    }

    [Fact]
    public void RunningDesired_DigestDrift_BlueGreenReplace()
    {
        var action = Single(
            new[] { Desired("svc-a", digest: "sha256:v2") },
            new[] { Container("svc-a", digest: "sha256:v1") });

        Assert.Equal(ReconcileActionKind.BlueGreenReplace, action.Kind);
        Assert.Contains("digest drift", action.Reason);
        Assert.NotNull(action.Desired);
        Assert.NotNull(action.Actual);
    }

    [Fact]
    public void RunningDesired_EnvDrift_BlueGreenReplace()
    {
        var action = Single(
            new[] { Desired("svc-a", digest: "sha256:v1", envHash: "env-2") },
            new[] { Container("svc-a", digest: "sha256:v1", envHash: "env-1") });

        Assert.Equal(ReconcileActionKind.BlueGreenReplace, action.Kind);
        Assert.Contains("env drift", action.Reason);
    }

    [Fact]
    public void RunningDesired_NullDesiredDigest_NoDigestChurn()
    {
        // A manifest whose digest has never been resolved must not force a replace
        // just because we cannot compare digests.
        var action = Single(
            new[] { Desired("svc-a", digest: null, envHash: "env-1") },
            new[] { Container("svc-a", digest: "sha256:v1", envHash: "env-1") });

        Assert.Equal(ReconcileActionKind.None, action.Kind);
    }

    [Fact]
    public void RestartRequested_ContainerOlder_BlueGreenReplace()
    {
        // A running container that started before restart_requested_at is stale: same
        // digest/env, but the restart request forces a zero-downtime recreate (§4.5).
        var action = Single(
            new[] { Desired("svc-a", restartRequestedAt: T0) },
            new[] { Container("svc-a", startedAt: T0 - TimeSpan.FromMinutes(1)) });

        Assert.Equal(ReconcileActionKind.BlueGreenReplace, action.Kind);
        Assert.Contains("restart requested", action.Reason);
    }

    [Fact]
    public void RestartRequested_ContainerStartedAfter_None()
    {
        // A container started after the request already satisfies it — it must NOT drift,
        // else the freshly-recreated container would be replaced every loop (§4.5, #3).
        var action = Single(
            new[] { Desired("svc-a", restartRequestedAt: T0) },
            new[] { Container("svc-a", startedAt: T0 + TimeSpan.FromSeconds(1)) });

        Assert.Equal(ReconcileActionKind.None, action.Kind);
    }

    [Fact]
    public void RestartRequested_ContainerWithinTolerance_None()
    {
        // Small clock skew: a container that started just before the request (inside the
        // tolerance window) counts as satisfying it — no restart loop.
        var action = Single(
            new[] { Desired("svc-a", restartRequestedAt: T0) },
            new[] { Container("svc-a", startedAt: T0 - (ReconcilePlanner.RestartTolerance - TimeSpan.FromSeconds(1))) });

        Assert.Equal(ReconcileActionKind.None, action.Kind);
    }

    [Fact]
    public void RestartRequested_ContainerJustBeyondTolerance_BlueGreenReplace()
    {
        // Started clearly before the request (beyond the tolerance): stale → recreate.
        var action = Single(
            new[] { Desired("svc-a", restartRequestedAt: T0) },
            new[] { Container("svc-a", startedAt: T0 - (ReconcilePlanner.RestartTolerance + TimeSpan.FromSeconds(1))) });

        Assert.Equal(ReconcileActionKind.BlueGreenReplace, action.Kind);
        Assert.Contains("restart requested", action.Reason);
    }

    [Fact]
    public void RestartRequested_UnknownStartedAt_None()
    {
        // A container whose StartedAt is unknown is not provably stale, so a restart
        // request does not force a replace (which could otherwise loop forever).
        var plan = ReconcilePlanner.Plan(
            new[] { Desired("svc-a", restartRequestedAt: T0) },
            new[] { new ContainerInfo("svc-a", "registry/svc-a", "sha256:v1", "running", null, "env-1") });

        var action = Assert.Single(plan);
        Assert.Equal(ReconcileActionKind.None, action.Kind);
    }

    [Fact]
    public void NoRestartRequested_OldContainer_None()
    {
        // With no restart_requested_at, an old StartedAt is irrelevant — only digest/env
        // drive drift, so a matching container is converged.
        var action = Single(
            new[] { Desired("svc-a", restartRequestedAt: null) },
            new[] { Container("svc-a", startedAt: DateTimeOffset.UnixEpoch) });

        Assert.Equal(ReconcileActionKind.None, action.Kind);
    }

    [Fact]
    public void RestartRequested_StoppedService_StillStopRemove()
    {
        // restart_requested_at only matters for a service desired running; a stopped
        // service with a container is still torn down regardless of the stamp.
        var action = Single(
            new[] { Desired("svc-a", status: "stopped", restartRequestedAt: T0) },
            new[] { Container("svc-a", startedAt: T0 - TimeSpan.FromMinutes(1)) });

        Assert.Equal(ReconcileActionKind.StopRemove, action.Kind);
    }

    [Fact]
    public void StoppedDesired_ContainerPresent_StopRemove()
    {
        var action = Single(
            new[] { Desired("svc-a", status: "stopped") },
            new[] { Container("svc-a") });

        Assert.Equal(ReconcileActionKind.StopRemove, action.Kind);
        Assert.NotNull(action.Actual);
    }

    [Fact]
    public void StoppedDesired_NoContainer_None()
    {
        var action = Single(
            new[] { Desired("svc-a", status: "stopped") },
            Array.Empty<ContainerInfo>());

        Assert.Equal(ReconcileActionKind.None, action.Kind);
    }

    [Fact]
    public void OrphanContainer_NoDesiredEntry_StopRemove()
    {
        var action = Single(
            Array.Empty<DesiredService>(),
            new[] { Container("svc-ghost") });

        Assert.Equal(ReconcileActionKind.StopRemove, action.Kind);
        Assert.Equal("svc-ghost", action.ServiceName);
        Assert.Null(action.Desired);
        Assert.Contains("orphan", action.Reason);
    }

    [Fact]
    public void GreenCandidateContainers_AreIgnored()
    {
        // A transient {name}-green from an in-flight swap must not be treated as an
        // orphan, nor satisfy the desired canonical container.
        var plan = ReconcilePlanner.Plan(
            new[] { Desired("svc-a") },
            new[] { Container("svc-a-green") });

        var action = Assert.Single(plan);
        Assert.Equal(ReconcileActionKind.Start, action.Kind);
        Assert.Equal("svc-a", action.ServiceName);
    }

    [Fact]
    public void ExitedContainerStillCounts_DigestMatch_None()
    {
        // State does not drive the plan; digest/env identity does. An exited
        // container with matching identity is "converged" — restart policy revives it.
        var action = Single(
            new[] { Desired("svc-a") },
            new[] { Container("svc-a", state: "exited") });

        Assert.Equal(ReconcileActionKind.None, action.Kind);
    }

    [Fact]
    public void Plan_IsOrderedByKind_ThenName()
    {
        var desired = new[]
        {
            Desired("z-start"),                                   // Start
            Desired("a-start"),                                   // Start
            Desired("b-replace", digest: "sha256:v2"),            // BlueGreenReplace
            Desired("c-stop", status: "stopped"),                 // StopRemove
            Desired("d-none"),                                    // None
        };
        var actual = new[]
        {
            Container("b-replace", digest: "sha256:v1"),
            Container("c-stop"),
            Container("d-none"),
            Container("orphan-x"),                                // StopRemove (orphan)
        };

        var plan = ReconcilePlanner.Plan(desired, actual);

        // StopRemove group first (by name), then Start (by name), then BlueGreen, then None.
        Assert.Collection(plan,
            a => Assert.Equal((ReconcileActionKind.StopRemove, "c-stop"), (a.Kind, a.ServiceName)),
            a => Assert.Equal((ReconcileActionKind.StopRemove, "orphan-x"), (a.Kind, a.ServiceName)),
            a => Assert.Equal((ReconcileActionKind.Start, "a-start"), (a.Kind, a.ServiceName)),
            a => Assert.Equal((ReconcileActionKind.Start, "z-start"), (a.Kind, a.ServiceName)),
            a => Assert.Equal((ReconcileActionKind.BlueGreenReplace, "b-replace"), (a.Kind, a.ServiceName)),
            a => Assert.Equal((ReconcileActionKind.None, "d-none"), (a.Kind, a.ServiceName)));
    }

    [Fact]
    public void EmptyInputs_EmptyPlan()
    {
        Assert.Empty(ReconcilePlanner.Plan(Array.Empty<DesiredService>(), Array.Empty<ContainerInfo>()));
    }

    [Fact]
    public void MissingActualEnvHash_TreatedAsDrift()
    {
        // A container without a recorded env hash (e.g. not started by us) differs
        // from any desired hash and is brought under management via a replace.
        var action = Single(
            new[] { Desired("svc-a", digest: "sha256:v1", envHash: "env-1") },
            new[] { Container("svc-a", digest: "sha256:v1", envHash: null) });

        Assert.Equal(ReconcileActionKind.BlueGreenReplace, action.Kind);
        Assert.Contains("env drift", action.Reason);
    }
}
