namespace Gateway.Api.Jobs;

/// <summary>
/// Tunables for the job scheduler (docs/jobs.md section 3), bound from the
/// <c>Jobs</c> configuration section. The network, log driver and heartbeat stale
/// threshold are not repeated here: the scheduler reads them from
/// <see cref="Reconcile.ReconcilerOptions"/> so jobs and services agree.
/// </summary>
public sealed class JobSchedulerOptions
{
    /// <summary>Time between scheduler ticks (<c>Jobs:TickInterval</c>, default 10 s).</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Most runs one instance drives at once (<c>Jobs:MaxConcurrent</c>, default 4).
    /// The leader claims no more queued runs than it has free slots, so the rest wait
    /// in <c>queued</c>.
    /// </summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Memory limit of a job container in bytes (<c>Jobs:MemoryLimitBytes</c>, default 1 GiB).</summary>
    public long MemoryLimitBytes { get; set; } = 1L << 30;

    /// <summary>
    /// The CloudWatch agent's EMF listener the run metrics are sent to over UDP
    /// (<c>Jobs:MetricsEndpoint</c>, default <c>127.0.0.1:25888</c>). Empty turns the
    /// send off; the in-process meter still records.
    /// </summary>
    public string MetricsEndpoint { get; set; } = "127.0.0.1:25888";

    /// <summary>CloudWatch namespace of the run metrics (<c>Jobs:MetricsNamespace</c>, default <c>Gateway</c>).</summary>
    public string MetricsNamespace { get; set; } = "Gateway";
}
