using System.Net.Http.Headers;
using Gateway.Api.Data;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// A <see cref="ManagementApiFactory"/> for the job endpoints with a recording
/// publisher (so <c>ops:jobs</c> events can be asserted) and an optional fixed job
/// clock (so <c>next_run_at</c> is deterministic).
/// </summary>
public sealed class JobTestHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _app;

    public JobTestHost(TimeProvider? clock = null)
    {
        _app = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChannelEventPublisher>();
            services.AddSingleton<IChannelEventPublisher>(Events);

            if (clock is not null)
            {
                services.AddKeyedSingleton(JobEndpoints.ClockKey, clock);
            }
        }));
    }

    public ManagementApiFactory Factory { get; } = new();

    public RecordingPublisher Events { get; } = new();

    /// <summary>A client carrying <paramref name="token"/>, or anonymous when null.</summary>
    public HttpClient Client(string? token)
    {
        var client = _app.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public HttpClient Admin() => Client(ManagementApiFactory.AdminToken());

    /// <summary>Seed rows directly (jobs, script versions, runs).</summary>
    public Task SeedAsync(params object[] rows) => Factory.WithDbAsync(async db =>
    {
        db.AddRange(rows);
        await db.SaveChangesAsync();
    });

    public ValueTask DisposeAsync() => Factory.DisposeAsync();

    public static Job Job(string name, string script = "echo hi", string? schedule = "0 4 * * *", bool enabled = true) => new()
    {
        Name = name,
        Schedule = schedule,
        TimeZone = "UTC",
        Script = script,
        ScriptVersion = 1,
        Image = "gateway-job-runner",
        Tag = "latest",
        TimeoutSeconds = 3600,
        Enabled = enabled,
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    public static JobScriptVersion Version(string job, int version, string script) => new()
    {
        Job = job,
        Version = version,
        Script = script,
        Sha256 = JobEndpoints.Sha256Hex(script),
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    public static JobRun Run(long id, string job, string status, string? instanceId = null) => new()
    {
        Id = id,
        Job = job,
        Trigger = JobRunTrigger.Manual,
        Actor = "seed",
        Status = status,
        InstanceId = instanceId,
        ScriptVersion = 1,
        QueuedAt = DateTimeOffset.UnixEpoch,
        StartedAt = status == JobRunStatus.Queued ? null : DateTimeOffset.UnixEpoch,
        FinishedAt = JobRunStatus.IsActive(status) ? null : DateTimeOffset.UnixEpoch,
    };

    /// <summary>Records every <c>TryPublish</c> so a test can assert the envelope.</summary>
    public sealed class RecordingPublisher : IChannelEventPublisher
    {
        private readonly object _gate = new();
        private readonly List<Published> _published = new();

        public sealed record Published(string Channel, string Event, object Data);

        public IReadOnlyList<Published> All
        {
            get
            {
                lock (_gate)
                {
                    return _published.ToList();
                }
            }
        }

        public Task PublishAsync(string channel, string @event, object data, CancellationToken ct = default)
        {
            TryPublish(channel, @event, data);
            return Task.CompletedTask;
        }

        public void TryPublish(string channel, string @event, object data)
        {
            lock (_gate)
            {
                _published.Add(new Published(channel, @event, data));
            }
        }

        public IEnumerable<Published> OnJobs(string @event) =>
            All.Where(p => p.Channel == JobEndpoints.OpsJobsChannel && p.Event == @event);
    }
}
