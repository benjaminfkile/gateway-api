namespace Gateway.Api.Resources;

/// <summary>
/// How the gateway starts a fleet instance refresh and how it tells one is in flight
/// (docs/load.md section 9). Starting follows the CI deploy script: raise the group's
/// max by one for launch-before-terminate overlap, start the refresh with the deploy
/// preferences, and put the max back at once if the start fails. On success the max
/// stays raised until <see cref="FleetRefreshWatcher"/> sees the refresh end.
/// </summary>
public static class FleetRefreshes
{
    /// <summary>A started refresh and the max the watcher restores when it ends.</summary>
    /// <param name="RefreshId">The Auto Scaling refresh id.</param>
    /// <param name="RestoreMax">The group's max before it was raised by one.</param>
    public sealed record Started(string RefreshId, int RestoreMax);

    /// <summary>The group's newest refresh when it is still in flight, else null.</summary>
    public static async Task<FleetRefresh?> InFlightAsync(IFleetScaler scaler, string group, CancellationToken ct = default)
    {
        var newest = (await scaler.DescribeInstanceRefreshesAsync(group, ct: ct)).FirstOrDefault();
        return newest is { IsTerminal: false } ? newest : null;
    }

    /// <summary>
    /// Raise <paramref name="group"/>'s max by one from <paramref name="size"/> and start a
    /// refresh with <see cref="FleetRefreshPreferences.Deploy"/>. When the start throws,
    /// the max is set back to <paramref name="size"/>'s max before the exception goes on.
    /// </summary>
    public static async Task<Started> StartAsync(IFleetScaler scaler, string group, FleetGroupSize size, CancellationToken ct = default)
    {
        await scaler.SetGroupSizeAsync(group, size.Min, size.Desired, size.Max + 1, ct);
        try
        {
            var refreshId = await scaler.StartInstanceRefreshAsync(group, FleetRefreshPreferences.Deploy, ct);
            return new Started(refreshId, size.Max);
        }
        catch
        {
            try
            {
                await scaler.SetGroupSizeAsync(group, size.Min, size.Desired, size.Max, CancellationToken.None);
            }
            catch (Exception)
            {
                // The start failure is the error the caller reports; a failed restore
                // leaves the max one higher until the next fleet size change sets it.
            }

            throw;
        }
    }
}
