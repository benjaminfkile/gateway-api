using Gateway.Api.Containers;

namespace Gateway.Api.Reconcile;

/// <summary>What the reconciler should do for one service on this box.</summary>
public enum ReconcileActionKind
{
    /// <summary>Already converged — nothing to do.</summary>
    None,

    /// <summary>Nothing is running for a service that should be — start it.</summary>
    Start,

    /// <summary>A container is running that should not be — stop and remove it.</summary>
    StopRemove,

    /// <summary>
    /// A container is running but on the wrong digest/env — replace it with zero
    /// downtime via the blue-green sequence (tech-spec §7): the old container
    /// keeps serving until the new one is healthy.
    /// </summary>
    BlueGreenReplace,
}

/// <summary>
/// One ordered step in a reconcile plan produced by <see cref="ReconcilePlanner"/>.
/// A plan is pure data — the <see cref="ReconcilerService"/> is what executes it.
/// </summary>
/// <param name="Kind">The operation to perform.</param>
/// <param name="ServiceName">The service this step concerns.</param>
/// <param name="Desired">
/// The desired state driving the step, present for <see cref="ReconcileActionKind.Start"/>,
/// <see cref="ReconcileActionKind.BlueGreenReplace"/> and <see cref="ReconcileActionKind.None"/>;
/// null for a <see cref="ReconcileActionKind.StopRemove"/> of an orphaned container.
/// </param>
/// <param name="Actual">The running container the step acts on, when one exists.</param>
/// <param name="Reason">Human-readable explanation, for structured logs and audit.</param>
public sealed record ReconcileAction(
    ReconcileActionKind Kind,
    string ServiceName,
    DesiredService? Desired,
    ContainerInfo? Actual,
    string Reason);
