using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Data;

namespace Gateway.Api.RealTime;

/// <summary>
/// The per-service publish secret plumbing (tech-spec §4.2, task #593). A channel
/// <c>{prefix}:{topic}</c> is owned by the manifest service named <c>prefix</c>, and
/// only that owner may publish to it through <c>POST /internal/publish</c>. The owner
/// proves itself with a random, url-safe token stored on its manifest row
/// (<see cref="ServiceManifest.RealtimePublishToken"/>) and injected into its
/// container as <c>GATEWAY_REALTIME_TOKEN</c>.
/// </summary>
public static class RealtimePublishToken
{
    /// <summary>Environment variable the token is exposed to a managed container as.</summary>
    public const string ContainerEnvVar = "GATEWAY_REALTIME_TOKEN";

    /// <summary>Request header a downstream presents its publish token on.</summary>
    public const string Header = "X-Gateway-Realtime-Token";

    // 32 bytes of CSPRNG entropy — comfortably past the "32+ bytes" floor and, encoded
    // url-safe, safe to carry verbatim in an HTTP header and a container env value.
    private const int TokenBytes = 32;

    /// <summary>Generate a fresh cryptographically-random, url-safe publish token.</summary>
    public static string Generate() =>
        Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>
    /// Ensure <paramref name="manifest"/> carries a publish token before it is
    /// persisted: preserve the token already on the row (<paramref name="existing"/>)
    /// across an edit that does not carry one, and mint a new one when neither the
    /// incoming write nor the stored row has a token yet (a fresh create, or a
    /// pre-migration row upserted for the first time since the column was added). This
    /// is the single choke point for every manifest write, so a token is generated
    /// exactly once and never rotates on an unrelated edit.
    /// </summary>
    public static void Ensure(ServiceManifest manifest, ServiceManifest? existing)
    {
        if (string.IsNullOrEmpty(manifest.RealtimePublishToken))
        {
            manifest.RealtimePublishToken = existing?.RealtimePublishToken;
        }

        if (string.IsNullOrEmpty(manifest.RealtimePublishToken))
        {
            manifest.RealtimePublishToken = Generate();
        }
    }

    /// <summary>
    /// Constant-time equality of a presented token against a service's stored token.
    /// Length-agnostic and never short-circuits on the first differing byte, so it
    /// leaks neither the token's length nor how far a guess matched. A null/empty
    /// stored token (pre-migration row) never matches — the caller rejects it.
    /// </summary>
    public static bool Matches(string? presented, string? stored)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(stored));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
