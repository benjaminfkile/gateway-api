using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gateway.Api.Load;

/// <summary>
/// The JSON text columns of <c>load_run</c> (docs/load.md section 2): the
/// <c>timeline</c> array, the <c>checkins</c> map and the <c>peaks</c> map. Shared by
/// every store so the EF store and the fakes write the same shapes.
/// </summary>
public static class LoadJson
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>One <c>timeline</c> entry.</summary>
    public sealed record TimelineEntry(DateTimeOffset At, string Status, string By, string? Note);

    /// <summary>A timeline with one more entry. A broken or empty column starts a new array.</summary>
    public static string AppendTimeline(string? timeline, DateTimeOffset at, string status, string by, string? note = null)
    {
        var entries = ParseTimeline(timeline).ToList();
        entries.Add(new TimelineEntry(at, status, by, note));
        return JsonSerializer.Serialize(entries, Web);
    }

    /// <summary>The timeline entries, oldest first; empty for a broken column.</summary>
    public static IReadOnlyList<TimelineEntry> ParseTimeline(string? timeline)
    {
        if (string.IsNullOrWhiteSpace(timeline))
        {
            return Array.Empty<TimelineEntry>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<TimelineEntry>>(timeline, Web) ?? new List<TimelineEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<TimelineEntry>();
        }
    }

    /// <summary>The <c>checkins</c> map of instance id to generator index; empty for a broken column.</summary>
    public static Dictionary<string, int> ParseCheckins(string? checkins)
    {
        if (string.IsNullOrWhiteSpace(checkins))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(checkins);
            return map is null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(map, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    /// <summary>The <c>checkins</c> column for <paramref name="map"/>, keys in ordinal order.</summary>
    public static string WriteCheckins(IReadOnlyDictionary<string, int> map) =>
        JsonSerializer.Serialize(new SortedDictionary<string, int>(map.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal));

    /// <summary>The <c>peaks</c> map; empty for a broken column. Non-numeric values are skipped.</summary>
    public static Dictionary<string, double> ParsePeaks(string? peaks)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(peaks))
        {
            return map;
        }

        try
        {
            if (JsonNode.Parse(peaks) is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                {
                    if (value is JsonValue v && v.TryGetValue<double>(out var number))
                    {
                        map[key] = number;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return map;
    }

    /// <summary>
    /// <paramref name="peaks"/> with every key of <paramref name="sample"/> raised to the
    /// larger of the two values; keys in ordinal order.
    /// </summary>
    public static string MergePeaks(string? peaks, IReadOnlyDictionary<string, double> sample)
    {
        var map = ParsePeaks(peaks);
        foreach (var (key, value) in sample)
        {
            if (double.IsFinite(value) && (!map.TryGetValue(key, out var current) || value > current))
            {
                map[key] = value;
            }
        }

        var ordered = new JsonObject();
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            ordered[key] = map[key];
        }

        return ordered.ToJsonString();
    }

    /// <summary>
    /// A stored JSON text column as a <see cref="JsonElement"/> for a response body;
    /// <paramref name="fallback"/> when the text does not parse.
    /// </summary>
    public static JsonElement Element(string? json, string fallback = "{}")
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? fallback : json);
        }
        catch (JsonException)
        {
            return JsonSerializer.Deserialize<JsonElement>(fallback);
        }
    }

    /// <summary>A number formatted for CSV and messages: invariant, shortest round-trip form.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
