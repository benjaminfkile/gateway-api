using System.Text.Json.Nodes;
using Gateway.Api.Data;
using Gateway.Api.Load;

namespace Gateway.Api.Tests;

/// <summary>
/// The stats arithmetic of docs/load.md sections 4 and 8 as pure functions: the
/// fleet-wide aggregate (counters and gauges summed, trends by the worst value, rates
/// recomputed), the peaks leaves, line validation, downsampling, CSV flattening, and
/// the JSON columns of <c>load_run</c>.
/// </summary>
public class LoadStatsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 2, 0, 0, TimeSpan.Zero);

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static double Num(JsonNode? node) => node!.GetValue<double>();

    [Fact]
    public void Aggregate_SumsCountersAndGauges_TakesTheWorstOfTrends()
    {
        var aggregate = LoadStats.Aggregate(new[]
        {
            Obj("""{"vus":480,"metrics":{"vus":{"type":"gauge","value":480},"ws_sessions":{"type":"counter","count":480,"rate":2},"event_lag_ms":{"type":"trend","avg":41,"min":12,"med":38,"max":410,"p(95)":120,"p(99)":310}}}"""),
            Obj("""{"vus":20,"metrics":{"vus":{"type":"gauge","value":20},"ws_sessions":{"type":"counter","count":20,"rate":0.5},"event_lag_ms":{"type":"trend","avg":90,"min":30,"med":20,"max":100,"p(95)":150,"p(99)":200}}}"""),
        });

        Assert.Equal(2, aggregate["generators"]!.GetValue<int>());
        Assert.Equal(500, Num(aggregate["vus"]));
        var metrics = aggregate["metrics"]!;
        Assert.Equal(500, Num(metrics["vus"]!["value"]));
        Assert.Equal("gauge", metrics["vus"]!["type"]!.GetValue<string>());
        Assert.Equal(500, Num(metrics["ws_sessions"]!["count"]));
        Assert.Equal(2.5, Num(metrics["ws_sessions"]!["rate"]));
        var lag = metrics["event_lag_ms"]!;
        Assert.Equal(90, Num(lag["avg"]));
        Assert.Equal(12, Num(lag["min"]));
        Assert.Equal(38, Num(lag["med"]));
        Assert.Equal(410, Num(lag["max"]));
        Assert.Equal(150, Num(lag["p(95)"]));
        Assert.Equal(310, Num(lag["p(99)"]));
    }

    [Fact]
    public void Aggregate_RatesSumPassesAndFails_UnknownTypesTakeTheMaximum_AMetricOnOneLineIsKept()
    {
        var aggregate = LoadStats.Aggregate(new[]
        {
            Obj("""{"vus":1,"metrics":{"checks":{"type":"rate","rate":1,"passes":30,"fails":0},"odd":{"type":"histogram","value":4},"only_here":{"type":"counter","count":7}}}"""),
            Obj("""{"vus":1,"metrics":{"checks":{"type":"rate","rate":0.5,"passes":5,"fails":5},"odd":{"type":"histogram","value":9},"broken":5}}"""),
            Obj("""{"vus":1}"""),
        });

        var metrics = aggregate["metrics"]!.AsObject();
        Assert.Equal(3, aggregate["generators"]!.GetValue<int>());
        Assert.Equal(35, Num(metrics["checks"]!["passes"]));
        Assert.Equal(5, Num(metrics["checks"]!["fails"]));
        Assert.Equal(35d / 40d, Num(metrics["checks"]!["rate"]));
        Assert.Equal(9, Num(metrics["odd"]!["value"]));
        Assert.Equal(7, Num(metrics["only_here"]!["count"]));
        Assert.False(metrics.ContainsKey("broken"));
    }

    [Fact]
    public void Aggregate_OfNothing_IsEmpty()
    {
        var aggregate = LoadStats.Aggregate(Array.Empty<JsonObject>());

        Assert.Equal(0, aggregate["generators"]!.GetValue<int>());
        Assert.Equal(0, Num(aggregate["vus"]));
        Assert.Empty(aggregate["metrics"]!.AsObject());
    }

    [Fact]
    public void MetricLeaves_KeysEveryNumericLeafMetricDotField()
    {
        var leaves = LoadStats.MetricLeaves(Obj("""{"event_lag_ms":{"type":"trend","p(99)":310,"min":12},"nested":{"a":{"b":2}},"flat":3,"text":{"note":"x"}}"""));

        Assert.Equal(new Dictionary<string, double>
        {
            ["event_lag_ms.p(99)"] = 310,
            ["event_lag_ms.min"] = 12,
            ["nested.a.b"] = 2,
            ["flat"] = 3,
        }, leaves);
    }

    [Fact]
    public void TryParseLine_AcceptsTheReportersShape_AndNormalisesTheTimeToUtc()
    {
        var ok = LoadStats.TryParseLine(
            """{"runId":41,"index":1,"at":"2026-11-05T04:14:05+02:00","vus":480,"metrics":{"vus":{"type":"gauge","value":480}}}""",
            41, 2, out var line, out var error, out var field);

        Assert.True(ok, error);
        Assert.Null(field);
        Assert.Equal(1, line!.Index);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 2, 14, 5, TimeSpan.Zero), line.At);
        Assert.Equal(TimeSpan.Zero, line.At.Offset);
        Assert.Equal(480, line.Vus);
        Assert.True(line.Metrics.ContainsKey("vus"));

        Assert.True(LoadStats.TryParseLine("""{"runId":41,"index":0,"at":"2026-11-05T02:14:05Z","vus":0}""", 41, 1, out var bare, out _, out _));
        Assert.Empty(bare!.Metrics);
    }

    private static LoadRunStat Stat(long id, string source, int? index, int second, string body) => new()
    {
        Id = id,
        RunId = 1,
        Source = source,
        Index = index,
        At = T0.AddSeconds(second),
        Body = body,
    };

    [Fact]
    public void Downsample_BucketsBySourceIndexAndStep_MaxOfNumbers_NewestOfTheRest()
    {
        var rows = new[]
        {
            Stat(1, LoadStatSource.Generator, 0, 1, """{"vus":10,"metrics":{"lag":{"type":"trend","p(99)":300}},"phase":"ramp"}"""),
            Stat(2, LoadStatSource.Generator, 0, 4, """{"vus":30,"metrics":{"lag":{"type":"trend","p(99)":100}},"phase":"hold"}"""),
            Stat(3, LoadStatSource.Generator, 1, 2, """{"vus":5}"""),
            Stat(4, LoadStatSource.Generator, 0, 6, """{"vus":1}"""),
            Stat(5, LoadStatSource.Fleet, null, 3, """{"cpu":40}"""),
        };

        var buckets = LoadStats.Downsample(rows, 5);

        Assert.Equal(4, buckets.Count);
        var first = buckets.Single(b => b.Source == LoadStatSource.Generator && b.Index == 0 && b.At == T0);
        Assert.Null(first.Id);
        Assert.Equal(30, Num(first.Body!["vus"]));
        Assert.Equal(300, Num(first.Body!["metrics"]!["lag"]!["p(99)"]));
        Assert.Equal("hold", first.Body!["phase"]!.GetValue<string>());
        Assert.Contains(buckets, b => b.Index == 0 && b.At == T0.AddSeconds(5) && Num(b.Body!["vus"]) == 1);
        Assert.Contains(buckets, b => b.Index == 1 && b.At == T0);
        Assert.Contains(buckets, b => b.Source == LoadStatSource.Fleet && b.Index is null);
        Assert.Equal(buckets.OrderBy(b => b.At).Select(b => b.At), buckets.Select(b => b.At));
    }

    [Fact]
    public void Csv_OneColumnPerLeaf_EmptyCellsForMissing_QuotesAwkwardNames()
    {
        var csv = LoadStats.Csv(LoadStats.Raw(new[]
        {
            Stat(1, LoadStatSource.Generator, 0, 0, """{"runId":1,"index":0,"vus":10,"metrics":{"a,b":{"count":2}}}"""),
            Stat(2, LoadStatSource.Fleet, null, 10, """{"cpu":40.25,"note":"text is not a column"}"""),
        }));

        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("at,source,index,cpu,\"metrics.a,b.count\",vus", lines[0]);
        Assert.Equal("2026-11-05T02:00:00.000Z,generator,0,,2,10", lines[1]);
        Assert.Equal("2026-11-05T02:00:10.000Z,fleet,,40.25,,", lines[2]);

        Assert.Equal("at,source,index\n", LoadStats.Csv(Array.Empty<LoadStatsRow>()));
    }

    [Fact]
    public void LoadJson_Peaks_Checkins_AndTimeline_SurviveBrokenColumns()
    {
        var peaks = LoadJson.MergePeaks("""{"b":5,"a":1}""", new Dictionary<string, double> { ["a"] = 3, ["b"] = 2, ["c"] = double.NaN });
        Assert.Equal("""{"a":3,"b":5}""", peaks);
        Assert.Equal("""{"x":1}""", LoadJson.MergePeaks("not json", new Dictionary<string, double> { ["x"] = 1 }));

        Assert.Empty(LoadJson.ParseCheckins("[oops"));
        Assert.Equal("""{"i-0a":0,"i-0b":1}""", LoadJson.WriteCheckins(new Dictionary<string, int> { ["i-0b"] = 1, ["i-0a"] = 0 }));

        var timeline = LoadJson.AppendTimeline("broken", T0, LoadRunStatus.Queued, "alice", "note");
        var entry = Assert.Single(LoadJson.ParseTimeline(timeline));
        Assert.Equal((T0, "queued", "alice", "note"), (entry.At, entry.Status, entry.By, entry.Note));
        Assert.Equal(2, LoadJson.ParseTimeline(LoadJson.AppendTimeline(timeline, T0, LoadRunStatus.GeneratorsUp, "leader")).Count);
    }
}
