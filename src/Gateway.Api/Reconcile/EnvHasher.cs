using System.Security.Cryptography;
using System.Text;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Computes a stable hash of a service's environment so env drift can be detected
/// by comparing a running container's stored hash against the desired one. The
/// hash is order-independent (keys are sorted) so equal environments always
/// produce the same value regardless of dictionary iteration order.
/// </summary>
public static class EnvHasher
{
    /// <summary>A short hex SHA-256 over the sorted <c>key=value</c> pairs.</summary>
    public static string Compute(IReadOnlyDictionary<string, string> env)
    {
        var builder = new StringBuilder();
        foreach (var kvp in env.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(kvp.Key).Append('=').Append(kvp.Value).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
