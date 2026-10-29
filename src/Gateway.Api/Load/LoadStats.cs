using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Data;

namespace Gateway.Api.Load;

/// <summary>A generator stats line that passed validation (docs/load.md section 4).</summary>
/// <param name="Index">The generator's zero-based index.</param>
/// <param name="At">When the line was taken.</param>
/// <param name="Vus">Virtual users the generator reports.</param>
/// <param name="Metrics">The k6 metrics object, empty when the line carries none.</param>
public sealed record LoadStatsLine(int Index, DateTimeOffset At, double Vus, JsonObject Metrics);

/// <summary>One row of a stats read: a stored line, or a downsampled bucket.</summary>
/// <param name="Id">The stored row id, or null for a downsampled bucket.</param>
/// <param name="Source"><c>generator</c> or <c>fleet</c>.</param>
/// <param name="Index">The generator index, or null for fleet samples.</param>
/// <param name="At">The line's time, or the bucket start.</param>
/// <param name="Body">The line, or the bucket's merge.</param>
public sealed record LoadStatsRow(long? Id, string Source, int? Index, DateTimeOffset At, JsonNode? Body);

/// <summary>
/// The stats arithmetic of docs/load.md sections 4 and 8: line validation, the peaks
/// leaves, the fleet-wide aggregate, downsampling and the CSV flattening. Pure
/// functions over JSON so each rule is unit-testable.
/// </summary>
public static class LoadStats
{
    /// <summary>
    /// Validate a generator stats line: a JSON object whose <c>runId</c> equals
    /// <paramref name="runId"/>, whose <c>index</c> is in <c>0..generators-1</c>, whose
    /// <c>at</c> is a timestamp and whose <c>vus</c> is a non-negative number;
    /// <c>metrics</c>, when present, must be an object. On failure
    /// <paramref name="error"/> and <paramref name="field"/> name the problem.
    /// </summary>
    public static bool TryParseLine(
        string body,
        long runId,
        int generators,
        out LoadStatsLine? line,
        out string? error,
        out string? field)
    {
        line = null;
        error = null;
        field = null;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            (error, field) = ("The stats line must be a JSON object.", "body");
            return false;
        }

        if (!TryNumber(root["runId"], out var id) || id != runId)
        {
            (error, field) = ($"runId is required and must be {runId}.", "runId");
            return false;
        }

        if (!TryNumber(root["index"], out var index) || index % 1 != 0 || index < 0 || index >= generators)
        {
            (error, field) = ($"index is required and must be an integer in 0..{generators - 1}.", "index");
            return false;
        }

        if (root["at"] is not JsonValue atValue
            || !atValue.TryGetValue<string>(out var atText)
            || !DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            (error, field) = ("at is required and must be an ISO 8601 timestamp.", "at");
            return false;
        }

        if (!TryNumber(root["vus"], out var vus) || vus < 0)
        {
            (error, field) = ("vus is required and must be a non-negative number.", "vus");
            return false;
        }

        JsonObject metrics;
        if (root["metrics"] is null)
        {
            metrics = new JsonObject();
        }
        else if (root["metrics"] is JsonObject m)
        {
            metrics = m;
        }
        else
        {
            (error, field) = ("metrics must be an object.", "metrics");
            return false;
        }

        line = new LoadStatsLine((int)index, at.ToUniversalTime(), vus, metrics);
        return true;
    }

    /// <summary>
    /// Every numeric leaf under <paramref name="metrics"/>, keyed <c>metric.field</c>
    /// (deeper nesting joins with further dots): the input of the run's peaks.
    /// </summary>
    public static Dictionary<string, double> MetricLeaves(JsonObject metrics)
    {
        var leaves = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, node) in metrics)
        {
            CollectLeaves(node, name, leaves);
        }

        return leaves;
    }

    /// <summary>
    /// The fleet-wide aggregate of the newest line of each generator (docs/load.md
    /// section 4): <c>{ generators, vus, metrics }</c>. Per metric type: counter and
    /// gauge fields are summed; trend fields take the worst value, which is the
    /// minimum for <c>min</c> and the maximum for every other field (avg, med, max,
    /// percentiles); rate metrics sum <c>passes</c> and <c>fails</c> and recompute
    /// <c>rate</c> from them; any other type takes the maximum of each field.
    /// </summary>
    public static JsonObject Aggregate(IEnumerable<JsonObject> lines)
    {
        var count = 0;
        double vus = 0;
        var merged = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            count++;
            if (TryNumber(line["vus"], out var lineVus))
            {
                vus += lineVus;
            }

            if (line["metrics"] is not JsonObject metrics)
            {
                continue;
            }

            foreach (var (name, node) in metrics)
            {
                if (node is not JsonObject metric)
                {
                    continue;
                }

                if (!merged.TryGetValue(name, out var into))
                {
                    merged[name] = (JsonObject)metric.DeepClone();
                    continue;
                }

                MergeMetric(into, metric);
            }
        }

        var result = new JsonObject
        {
            ["generators"] = count,
            ["vus"] = vus,
        };
        var outMetrics = new JsonObject();
        foreach (var (name, metric) in merged)
        {
            FinishMetric(metric);
            outMetrics[name] = metric;
        }

        result["metrics"] = outMetrics;
        return result;
    }

    /// <summary>
    /// Downsample <paramref name="rows"/> to one row per source, index and
    /// <paramref name="stepSeconds"/> bucket (aligned to the Unix epoch): each numeric
    /// leaf is the maximum within the bucket, any other leaf the newest value.
    /// </summary>
    public static IReadOnlyList<LoadStatsRow> Downsample(IReadOnlyList<LoadRunStat> rows, int stepSeconds)
    {
        var buckets = new Dictionary<(string Source, int? Index, long Start), JsonNode?>();
        foreach (var row in rows.OrderBy(r => r.At).ThenBy(r => r.Id))
        {
            var seconds = row.At.ToUnixTimeSeconds();
            var start = (long)Math.Floor(seconds / (double)stepSeconds) * stepSeconds;
            var key = (row.Source, row.Index, start);
            var body = ParseBody(row.Body);
            buckets[key] = buckets.TryGetValue(key, out var acc) ? MaxMerge(acc, body) : body;
        }

        return buckets
            .Select(kv => new LoadStatsRow(
                null,
                kv.Key.Source,
                kv.Key.Index,
                DateTimeOffset.FromUnixTimeSeconds(kv.Key.Start),
                kv.Value))
            .OrderBy(r => r.At)
            .ThenBy(r => r.Source, StringComparer.Ordinal)
            .ThenBy(r => r.Index ?? -1)
            .ToList();
    }

    /// <summary>Stored rows as read rows, bodies parsed.</summary>
    public static IReadOnlyList<LoadStatsRow> Raw(IReadOnlyList<LoadRunStat> rows) =>
        rows.Select(r => new LoadStatsRow(r.Id, r.Source, r.Index, r.At, ParseBody(r.Body))).ToList();

    /// <summary>
    /// The rows as CSV: <c>at,source,index</c> then one column per numeric leaf of any
    /// body (dotted paths, arrays by position, sorted), the top-level <c>runId</c> and
    /// <c>index</c> left out because they are columns already. A row without a leaf
    /// leaves that cell empty.
    /// </summary>
    public static string Csv(IReadOnlyList<LoadStatsRow> rows)
    {
        var flattened = rows.Select(r =>
        {
            var leaves = new Dictionary<string, double>(StringComparer.Ordinal);
            if (r.Body is JsonObject obj)
            {
                foreach (var (key, node) in obj)
                {
                    if (key is "runId" or "index")
                    {
                        continue;
                    }

                    CollectLeaves(node, key, leaves);
                }
            }
            else if (r.Body is not null)
            {
                CollectLeaves(r.Body, "value", leaves);
            }

            return (Row: r, Leaves: leaves);
        }).ToList();

        var columns = flattened
            .SelectMany(f => f.Leaves.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var csv = new StringBuilder();
        csv.Append("at,source,index");
        foreach (var column in columns)
        {
            csv.Append(',').Append(Escape(column));
        }

        csv.Append('\n');
        foreach (var (row, leaves) in flattened)
        {
            csv.Append(row.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(Escape(row.Source))
                .Append(',')
                .Append(row.Index?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            foreach (var column in columns)
            {
                csv.Append(',');
                if (leaves.TryGetValue(column, out var value))
                {
                    csv.Append(LoadJson.Number(value));
                }
            }

            csv.Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>
    /// The leaf-wise maximum of two bodies: numbers take the larger, objects merge
    /// key by key, anything else takes <paramref name="next"/>.
    /// </summary>
    public static JsonNode? MaxMerge(JsonNode? acc, JsonNode? next)
    {
        if (acc is JsonObject a && next is JsonObject n)
        {
            var result = (JsonObject)a.DeepClone();
            foreach (var (key, node) in n)
            {
                result[key] = result.ContainsKey(key) ? MaxMerge(result[key], node) : node?.DeepClone();
            }

            return result;
        }

        if (TryNumber(acc, out var x) && TryNumber(next, out var y))
        {
            return JsonValue.Create(Math.Max(x, y));
        }

        return next?.DeepClone() ?? acc?.DeepClone();
    }

    /// <summary>A stored body parsed for a response; a body that does not parse comes back as a string.</summary>
    public static JsonNode? ParseBody(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(body);
        }
    }

    // ---------- helpers ----------

    private static void MergeMetric(JsonObject into, JsonObject next)
    {
        var type = (into["type"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;

        foreach (var (field, node) in next)
        {
            if (!TryNumber(node, out var value))
            {
                if (!into.ContainsKey(field))
                {
                    into[field] = node?.DeepClone();
                }

                continue;
            }

            if (!TryNumber(into[field], out var current))
            {
                into[field] = value;
                continue;
            }

            into[field] = type switch
            {
                "counter" or "gauge" => current + value,
                "trend" => field == "min" ? Math.Min(current, value) : Math.Max(current, value),
                "rate" when field is "passes" or "fails" => current + value,
                _ => Math.Max(current, value),
            };
        }
    }

    private static void FinishMetric(JsonObject metric)
    {
        // A rate metric's rate is the merged pass fraction when both counts are known.
        if ((metric["type"] as JsonValue)?.TryGetValue<string>(out var type) == true
            && type == "rate"
            && TryNumber(metric["passes"], out var passes)
            && TryNumber(metric["fails"], out var fails)
            && passes + fails > 0)
        {
            metric["rate"] = passes / (passes + fails);
        }
    }

    private static void CollectLeaves(JsonNode? node, string path, Dictionary<string, double> leaves)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    CollectLeaves(child, path + "." + key, leaves);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    CollectLeaves(array[i], path + "." + i.ToString(CultureInfo.InvariantCulture), leaves);
                }

                break;
            default:
                if (TryNumber(node, out var value))
                {
                    leaves[path] = value;
                }

                break;
        }
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (v.TryGetValue<double>(out var d))
        {
            value = d;
        }
        else if (v.TryGetValue<long>(out var l))
        {
            value = l;
        }
        else if (v.TryGetValue<int>(out var i))
        {
            value = i;
        }
        else if (v.TryGetValue<decimal>(out var m))
        {
            value = (double)m;
        }
        else
        {
            return false;
        }

        return double.IsFinite(value);
    }

    private static string Escape(string cell) =>
        cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + cell.Replace("\"", "\"\"") + "\""
            : cell;
}
