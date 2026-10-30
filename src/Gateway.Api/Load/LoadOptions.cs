using Gateway.Api.Management;

namespace Gateway.Api.Load;

/// <summary>
/// Load run configuration (docs/load.md section 12), bound from the <c>Load</c>
/// configuration section. Resolved lazily from <see cref="IConfiguration"/> so a test
/// host's late-bound settings are honoured.
/// </summary>
public sealed class LoadOptions
{
    /// <summary>Configuration section the options bind from.</summary>
    public const string SectionName = "Load";

    /// <summary>
    /// Generator Auto Scaling group name (<c>Load:GeneratorGroup</c>). Unset disables
    /// Start with <c>503 load_unconfigured</c>.
    /// </summary>
    public string? GeneratorGroup { get; set; }

    /// <summary>Secrets Manager name of the generator spec secret (<c>Load:GeneratorSpecSecret</c>).</summary>
    public string? GeneratorSpecSecret { get; set; }

    /// <summary>Most generators a run may ask for (<c>Load:MaxGenerators</c>, default 2).</summary>
    public int MaxGenerators { get; set; } = 2;

    /// <summary>How long <c>generators_up</c> waits for every generator (<c>Load:CheckInTimeout</c>, default 5 min).</summary>
    public TimeSpan CheckInTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Ramp or hold with no generator line for this long marks the run lost (<c>Load:StatsStaleAfter</c>, default 1 min).</summary>
    public TimeSpan StatsStaleAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Hold of a validation run in seconds (<c>Load:ValidationHoldSeconds</c>, default 300).</summary>
    public int ValidationHoldSeconds { get; set; } = 300;

    /// <summary>How often the leader writes a fleet sample (<c>Load:FleetSampleInterval</c>, default 10 s).</summary>
    public TimeSpan FleetSampleInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Time between load scheduler ticks (<c>Load:TickInterval</c>, default 10 s).</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The public origin the generators reach the gateway at, e.g.
    /// <c>https://gateway.example.com</c> (<c>Load:GatewayBaseUrl</c>). Written into the
    /// generator spec secret as <c>gatewayBaseUrl</c>; a run cannot bring generators up
    /// while it is unset.
    /// </summary>
    public string? GatewayBaseUrl { get; set; }

    /// <summary>
    /// Consecutive failed ticks of one run's scaling or secret step before the run is
    /// failed (<c>Load:ScaleRetryLimit</c>, default 6).
    /// </summary>
    public int ScaleRetryLimit { get; set; } = 6;

    /// <summary>
    /// Runner image a scenario gets when PUT omits it (<c>Load:DefaultImage</c>; unset
    /// falls back to <c>Jobs:DefaultImage</c>, then <see cref="JobEndpoints.FallbackImage"/>).
    /// </summary>
    public string DefaultImage { get; set; } = JobEndpoints.FallbackImage;

    /// <summary>
    /// Runner tag a scenario gets when PUT omits it (<c>Load:DefaultTag</c>; unset falls
    /// back to <c>Jobs:DefaultTag</c>, then <see cref="JobEndpoints.FallbackTag"/>).
    /// </summary>
    public string DefaultTag { get; set; } = JobEndpoints.FallbackTag;

    /// <summary>True when <see cref="GeneratorGroup"/> is set, so Start is possible.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(GeneratorGroup);

    /// <summary>Bind the <c>Load</c> section, applying the job defaults for image and tag.</summary>
    public static LoadOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new LoadOptions
        {
            DefaultImage = NonEmpty(configuration[JobEndpoints.DefaultImageKey]) ?? JobEndpoints.FallbackImage,
            DefaultTag = NonEmpty(configuration[JobEndpoints.DefaultTagKey]) ?? JobEndpoints.FallbackTag,
        };
        configuration.GetSection(SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.DefaultImage))
        {
            options.DefaultImage = JobEndpoints.FallbackImage;
        }

        if (string.IsNullOrWhiteSpace(options.DefaultTag))
        {
            options.DefaultTag = JobEndpoints.FallbackTag;
        }

        if (options.MaxGenerators < 1)
        {
            options.MaxGenerators = 1;
        }

        if (options.ScaleRetryLimit < 1)
        {
            options.ScaleRetryLimit = 1;
        }

        if (options.TickInterval <= TimeSpan.Zero)
        {
            options.TickInterval = TimeSpan.FromSeconds(10);
        }

        options.GatewayBaseUrl = NonEmpty(options.GatewayBaseUrl)?.TrimEnd('/');

        return options;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
