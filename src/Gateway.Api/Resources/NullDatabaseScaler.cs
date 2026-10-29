namespace Gateway.Api.Resources;

/// <summary>
/// The <see cref="IDatabaseScaler"/> registered when <c>Resources:DatabaseInstance</c>
/// is unset. Every member throws <see cref="NotSupportedException"/>.
/// </summary>
public sealed class NullDatabaseScaler : IDatabaseScaler
{
    /// <summary>The message every member throws with.</summary>
    public const string NotConfiguredMessage = "No database scaler is configured on this gateway.";

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<DatabaseInstanceState?> DescribeInstanceAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task ModifyInstanceClassAsync(string id, string instanceClass, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);
}
