namespace Gateway.Api.Resources;

/// <summary>
/// The documented Postgres <c>max_connections</c> per RDS instance class, which the
/// database class change uses to tell a downgrade (docs/load.md section 9). An
/// operator-maintained table: the RDS Postgres default is
/// <c>LEAST({DBInstanceClassMemory/9531392}, 5000)</c>, and each value here is that
/// formula over the class's nominal memory (the real <c>DBInstanceClassMemory</c> is a
/// little lower, so the live value is a little lower too). A class missing from the
/// table is unknown, and an unknown class is never a safe downgrade target. Add a row
/// here before adding a class to <c>Resources:DatabaseClasses</c>.
/// </summary>
public static class DatabaseClassConnections
{
    private const long BytesPerConnection = 9531392;
    private const int Ceiling = 5000;

    // Nominal memory in GiB per class.
    private static readonly Dictionary<string, int> MemoryGiB = new(StringComparer.OrdinalIgnoreCase)
    {
        ["db.t3.micro"] = 1,
        ["db.t3.small"] = 2,
        ["db.t3.medium"] = 4,
        ["db.t3.large"] = 8,
        ["db.t3.xlarge"] = 16,
        ["db.t3.2xlarge"] = 32,
        ["db.t4g.micro"] = 1,
        ["db.t4g.small"] = 2,
        ["db.t4g.medium"] = 4,
        ["db.t4g.large"] = 8,
        ["db.t4g.xlarge"] = 16,
        ["db.t4g.2xlarge"] = 32,
        ["db.m6g.large"] = 8,
        ["db.m6g.xlarge"] = 16,
        ["db.m6g.2xlarge"] = 32,
        ["db.m6g.4xlarge"] = 64,
        ["db.m7g.large"] = 8,
        ["db.m7g.xlarge"] = 16,
        ["db.m7g.2xlarge"] = 32,
        ["db.m7g.4xlarge"] = 64,
        ["db.r6g.large"] = 16,
        ["db.r6g.xlarge"] = 32,
        ["db.r6g.2xlarge"] = 64,
        ["db.r7g.large"] = 16,
        ["db.r7g.xlarge"] = 32,
        ["db.r7g.2xlarge"] = 64,
    };

    /// <summary>Every class in the table with its documented <c>max_connections</c>, ordered by name.</summary>
    public static IReadOnlyDictionary<string, int> All { get; } = MemoryGiB
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .ToDictionary(kv => kv.Key, kv => Formula(kv.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>The documented <c>max_connections</c> of <paramref name="instanceClass"/>, or null when unknown.</summary>
    public static int? For(string? instanceClass) =>
        instanceClass is not null && All.TryGetValue(instanceClass, out var max) ? max : null;

    private static int Formula(int gib) => (int)Math.Min(gib * 1024L * 1024L * 1024L / BytesPerConnection, Ceiling);
}
