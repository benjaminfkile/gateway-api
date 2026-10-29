using Gateway.Api.Instances;

namespace Gateway.Api.Resources;

/// <summary>
/// Where the fleet group name comes from when <c>Resources:FleetGroup</c> is unset:
/// the instance's own <c>aws:autoscaling:groupName</c> tag. Behind an interface so a
/// test never reaches the metadata service.
/// </summary>
public interface IFleetGroupSource
{
    /// <summary>The group name, or null when it cannot be read.</summary>
    Task<string?> TryGetAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="IFleetGroupSource"/> over the EC2 instance metadata service (IMDSv2):
/// reads <c>latest/meta-data/tags/instance/aws:autoscaling:groupName</c>, which needs
/// instance metadata tags enabled on the launch template. Uses the reconciler's
/// <c>imds</c> client and bounds every call to 2 s like <see cref="Ec2InstanceMetadata"/>;
/// off EC2 it answers null.
/// </summary>
public sealed class ImdsFleetGroupSource : IFleetGroupSource
{
    /// <summary>The metadata path of the Auto Scaling group tag.</summary>
    public const string TagPath = "latest/meta-data/tags/instance/aws:autoscaling:groupName";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _clients;

    public ImdsFleetGroupSource(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    public async Task<string?> TryGetAsync(CancellationToken ct = default)
    {
        var http = _clients.CreateClient(Ec2InstanceMetadata.HttpClientName);
        var baseAddress = http.BaseAddress ?? new Uri(Ec2InstanceMetadata.ImdsBaseAddress);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Put, new Uri(baseAddress, "latest/api/token"));
            tokenRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "60");
            using var tokenResponse = await http.SendAsync(tokenRequest, timeout.Token);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var token = (await tokenResponse.Content.ReadAsStringAsync(timeout.Token)).Trim();

            using var tagRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, TagPath));
            tagRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);
            using var tagResponse = await http.SendAsync(tagRequest, timeout.Token);
            if (!tagResponse.IsSuccessStatusCode)
            {
                // 404 when metadata tags are off or the instance is not in a group.
                return null;
            }

            var name = (await tagResponse.Content.ReadAsStringAsync(timeout.Token)).Trim();
            return name.Length == 0 ? null : name;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>
/// The fleet group name the Resources API and the pre-flight checks use:
/// <c>Resources:FleetGroup</c> when set; else, when <c>Resources:FleetTemplate</c> is set
/// (the fleet is managed but its group is not named), the instance's
/// <c>aws:autoscaling:groupName</c> tag through <see cref="IFleetGroupSource"/>; else
/// null. A tag read that succeeds is kept for the life of the process; a failed one
/// is retried at most every five minutes.
/// </summary>
public sealed class FleetGroupResolver
{
    /// <summary>How long a failed tag read is remembered before it is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly ResourcesOptions _options;
    private readonly IFleetGroupSource _source;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _resolved;
    private DateTimeOffset? _failedAt;

    public FleetGroupResolver(ResourcesOptions options, IFleetGroupSource source, TimeProvider clock)
    {
        _options = options;
        _source = source;
        _clock = clock;
    }

    /// <summary>The fleet group name, or null when the fleet is not configured or the tag cannot be read.</summary>
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (_options.FleetGroup is { } configured)
        {
            return configured;
        }

        if (_options.FleetTemplate is null)
        {
            return null;
        }

        if (_resolved is { } known)
        {
            return known;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_resolved is not null)
            {
                return _resolved;
            }

            var now = _clock.GetUtcNow();
            if (_failedAt is { } failed && now - failed < RetryAfter)
            {
                return null;
            }

            _resolved = await _source.TryGetAsync(ct);
            _failedAt = _resolved is null ? now : null;
            return _resolved;
        }
        finally
        {
            _gate.Release();
        }
    }
}
