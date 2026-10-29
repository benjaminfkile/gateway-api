namespace Gateway.Api.Data;

/// <summary>
/// One stats line of a load run (docs/load.md section 2): a generator's line as
/// received, or a fleet sample the leader wrote. Indexed on (run_id, at).
/// </summary>
public class LoadRunStat
{
    /// <summary>Surrogate primary key (identity).</summary>
    public long Id { get; set; }

    /// <summary>The run the line belongs to.</summary>
    public long RunId { get; set; }

    /// <summary><c>generator</c> or <c>fleet</c> (see <see cref="LoadStatSource"/>).</summary>
    public string Source { get; set; } = default!;

    /// <summary>Generator index, or null for fleet samples.</summary>
    public int? Index { get; set; }

    /// <summary>When the line was taken.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The stats line as received, at most 64 KiB.</summary>
    public string Body { get; set; } = default!;
}
