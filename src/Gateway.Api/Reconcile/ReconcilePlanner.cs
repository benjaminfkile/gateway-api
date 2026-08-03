using Gateway.Api.Containers;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Pure convergence logic (tech-spec §4.3): given the desired services and the
/// containers actually running on this box, produce an ordered plan of actions
/// to close the gap. Deliberately free of any I/O, clock, or randomness so it can
/// be unit-tested exhaustively; <see cref="ReconcilerService"/> executes what it
/// returns.
/// </summary>
public static class ReconcilePlanner
{
    /// <summary>
    /// Compute the reconcile plan.
    /// <para>Rules, per service:</para>
    /// <list type="bullet">
    /// <item>desired <c>stopped</c>, container present → <see cref="ReconcileActionKind.StopRemove"/>.</item>
    /// <item>desired <c>stopped</c>, no container → <see cref="ReconcileActionKind.None"/>.</item>
    /// <item>desired <c>running</c>, no container → <see cref="ReconcileActionKind.Start"/>.</item>
    /// <item>desired <c>running</c>, container present with digest or env drift → <see cref="ReconcileActionKind.BlueGreenReplace"/>.</item>
    /// <item>desired <c>running</c>, container present and matching → <see cref="ReconcileActionKind.None"/>.</item>
    /// </list>
    /// A managed container with no matching desired entry is an orphan and gets a
    /// <see cref="ReconcileActionKind.StopRemove"/>. Transient <c>-green</c>
    /// candidates from an in-flight (or crashed) blue-green are ignored here — the
    /// reconciler owns their lifecycle during the swap.
    /// <para>
    /// The result is ordered deterministically — StopRemove, then Start, then
    /// BlueGreenReplace, then None, and by service name within each group — so a
    /// plan is stable and easy to assert on.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ReconcileAction> Plan(
        IReadOnlyList<DesiredService> desired,
        IReadOnlyList<ContainerInfo> actual)
    {
        // Canonical containers only; blue-green candidates are not steady state.
        var byName = actual
            .Where(c => !ReconcileNaming.IsGreen(c.Name))
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var actions = new List<ReconcileAction>();
        var desiredNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in desired)
        {
            desiredNames.Add(d.Name);
            byName.TryGetValue(d.Name, out var container);

            if (!d.WantsRunning)
            {
                actions.Add(container is null
                    ? new ReconcileAction(ReconcileActionKind.None, d.Name, d, null,
                        "desired stopped; no container running")
                    : new ReconcileAction(ReconcileActionKind.StopRemove, d.Name, d, container,
                        "desired stopped; removing running container"));
                continue;
            }

            if (container is null)
            {
                actions.Add(new ReconcileAction(ReconcileActionKind.Start, d.Name, d, null,
                    "desired running; no container present"));
                continue;
            }

            var reason = DriftReason(d, container);
            actions.Add(reason is null
                ? new ReconcileAction(ReconcileActionKind.None, d.Name, d, container,
                    "converged")
                : new ReconcileAction(ReconcileActionKind.BlueGreenReplace, d.Name, d, container,
                    reason));
        }

        // Orphans: managed containers we no longer have a desired entry for.
        foreach (var container in byName.Values)
        {
            if (!desiredNames.Contains(container.Name))
            {
                actions.Add(new ReconcileAction(ReconcileActionKind.StopRemove, container.Name, null, container,
                    "no manifest entry; removing orphaned container"));
            }
        }

        return actions
            .OrderBy(a => OrderOf(a.Kind))
            .ThenBy(a => a.ServiceName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns why a running container drifts from desired, or null when it
    /// matches. Digest drift is only considered when a desired digest is known
    /// (a manifest whose digest has never been resolved does not force churn).
    /// </summary>
    private static string? DriftReason(DesiredService d, ContainerInfo container)
    {
        if (!string.IsNullOrEmpty(d.Digest) &&
            !string.Equals(d.Digest, container.Digest, StringComparison.Ordinal))
        {
            return $"digest drift: desired {d.Digest}, running {container.Digest ?? "<unknown>"}";
        }

        if (!string.Equals(d.EnvHash, container.EnvHash, StringComparison.Ordinal))
        {
            return $"env drift: desired {d.EnvHash ?? "<none>"}, running {container.EnvHash ?? "<none>"}";
        }

        return null;
    }

    private static int OrderOf(ReconcileActionKind kind) => kind switch
    {
        ReconcileActionKind.StopRemove => 0,
        ReconcileActionKind.Start => 1,
        ReconcileActionKind.BlueGreenReplace => 2,
        _ => 3,
    };
}
