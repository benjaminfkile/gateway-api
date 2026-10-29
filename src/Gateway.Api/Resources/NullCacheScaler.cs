namespace Gateway.Api.Resources;

/// <summary>
/// The <see cref="ICacheScaler"/> registered when <c>Resources:CacheReplicationGroup</c>
/// is unset. Every member throws <see cref="NotSupportedException"/>.
/// </summary>
public sealed class NullCacheScaler : ICacheScaler
{
    /// <summary>The message every member throws with.</summary>
    public const string NotConfiguredMessage = "No cache scaler is configured on this gateway.";

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<CacheReplicationGroup?> DescribeReplicationGroupAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task ModifyNodeTypeAsync(string id, string nodeType, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);
}
