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

    private static bool TryNormalizeOrigin(string entry, out string origin)
    {
        origin = string.Empty;

        // A wildcard anywhere would defeat the exact-match requirement of a
        // credentialed CORS policy, so reject it outright.
        if (entry.Contains('*'))
        {
            return false;
        }

        if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0))
        {
            return false;
        }

        // Canonical origin: scheme://host[:port], default 80/443 elided. Uri.Authority
        // already omits the default port and any userinfo (rejected above).
        origin = $"{uri.Scheme}://{uri.Authority}";
        return true;
    }
}
