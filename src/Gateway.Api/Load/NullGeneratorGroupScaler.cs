namespace Gateway.Api.Load;

/// <summary>
/// The <see cref="IGeneratorGroupScaler"/> registered when <c>Load:GeneratorGroup</c> is
/// unset (a dev box without AWS). Every member throws
/// <see cref="NotSupportedException"/>, so nothing is ever scaled; the
/// <c>generator_group</c> pre-flight check reports the group as unreadable (red).
/// </summary>
public sealed class NullGeneratorGroupScaler : IGeneratorGroupScaler
{
    /// <summary>The message every member throws with.</summary>
    public const string NotConfiguredMessage = "No generator group scaler is configured on this gateway.";

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<GeneratorGroupSize?> DescribeAsync(string group, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task SetDesiredAsync(string group, int desired, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);
}
