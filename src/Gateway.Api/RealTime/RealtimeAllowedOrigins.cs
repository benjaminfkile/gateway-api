namespace Gateway.Api.RealTime;

/// <summary>
/// Parsing, validation, and normalization for a service's
/// <c>realtime_allowed_origins</c> — the comma-separated list of exact browser
/// origins whose frontends may negotiate a SignalR connection to <c>/hub</c>
/// (tech-spec §4.2, task #595).
/// <para>
/// A valid entry is an absolute <c>http</c>/<c>https</c> origin — scheme + host +
/// optional port — with no path, query, fragment, userinfo, or wildcard. Because the
/// resulting set feeds a credentialed CORS policy that must return exact-match
/// decisions (never a wildcard), entries are normalized to their canonical origin form
/// (<c>scheme://host[:port]</c>, default ports elided) so they compare byte-for-byte
/// against the browser's <c>Origin</c> header.
/// </para>
/// </summary>
public static class RealtimeAllowedOrigins
{
    /// <summary>
    /// Split, validate, and normalize a raw comma-separated origins string.
    /// Returns <c>true</c> with the normalized, re-joined value in
    /// <paramref name="normalized"/> (null when the input is null/empty) when every
    /// entry is a well-formed origin; <c>false</c> with the offending entry in
    /// <paramref name="invalidEntry"/> otherwise.
    /// </summary>
    public static bool TryNormalize(string? raw, out string? normalized, out string? invalidEntry)
    {
        normalized = null;
        invalidEntry = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var canonical = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (!TryNormalizeOrigin(part, out var origin))
            {
                invalidEntry = part;
                return false;
            }

            if (!canonical.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                canonical.Add(origin);
            }
        }

        normalized = canonical.Count == 0 ? null : string.Join(',', canonical);
        return true;
    }

    /// <summary>
    /// Enumerate the normalized origins of an already-stored value. Silently skips any
    /// malformed entry (stored values passed validation at upsert, so this is only
    /// defensive against pre-migration/hand-edited rows).
    /// </summary>
    public static IEnumerable<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNormalizeOrigin(part, out var origin))
            {
                yield return origin;
            }
        }
    }

    /// <summary>
    /// The outcome of canonicalizing one configured origin entry: the original text, its
    /// canonical origin form (null when the entry is malformed and was dropped), and, on a
    /// drop, a human-readable reason. Used to give the <b>same</b> canonicalization to
    /// both CORS surfaces (the static <c>ops</c> /mgmt policy and the dynamic /hub policy)
    /// from a single <c>GATEWAY_CORS_ORIGINS</c> value, and to log every entry that was
    /// normalized or dropped at startup (task #607, origin parity).
    /// </summary>
    public readonly record struct CanonicalOrigin(string Original, string? Canonical, string? DropReason)
    {
        /// <summary>The entry was malformed and excluded from both CORS surfaces.</summary>
        public bool WasDropped => Canonical is null;

        /// <summary>The entry was valid but rewritten (e.g. a trailing slash or default port removed).</summary>
        public bool WasNormalized => Canonical is not null && !string.Equals(Original, Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonicalize a raw comma-separated <c>GATEWAY_CORS_ORIGINS</c> value into one
    /// per-entry result each, preserving input order and reporting drops with a reason.
    /// Callers feed the surviving <see cref="CanonicalOrigin.Canonical"/> values to both
    /// the /mgmt and /hub CORS surfaces so a single env value can never pass one and fail
    /// the other, and log the normalized/dropped entries.
    /// </summary>
    public static IReadOnlyList<CanonicalOrigin> CanonicalizeConfigured(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<CanonicalOrigin>();
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<CanonicalOrigin>(parts.Length);
        foreach (var part in parts)
        {
            results.Add(TryNormalizeOrigin(part, out var origin, out var reason)
                ? new CanonicalOrigin(part, origin, null)
                : new CanonicalOrigin(part, null, reason));
        }

        return results;
    }

    private static bool TryNormalizeOrigin(string entry, out string origin) =>
        TryNormalizeOrigin(entry, out origin, out _);

    private static bool TryNormalizeOrigin(string entry, out string origin, out string? reason)
    {
        origin = string.Empty;
        reason = null;

        // A wildcard anywhere would defeat the exact-match requirement of a
        // credentialed CORS policy, so reject it outright.
        if (entry.Contains('*'))
        {
            reason = "wildcards are not allowed in a credentialed CORS origin";
            return false;
        }

        if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri))
        {
            reason = "not an absolute URI";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = $"scheme '{uri.Scheme}' is not http or https";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0))
        {
            reason = "an origin must be scheme://host[:port] with no userinfo, path, query, or fragment";
            return false;
        }

        // Canonical origin: scheme://host[:port], default 80/443 elided. Uri.Authority
        // already omits the default port and any userinfo (rejected above).
        origin = $"{uri.Scheme}://{uri.Authority}";
        return true;
    }
}
