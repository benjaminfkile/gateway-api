namespace Gateway.Api.Health;

/// <summary>
/// Health of a single downstream service as reported in the aggregate. Property
/// names are serialized verbatim (<c>status</c>, <c>httpStatus</c>,
/// <c>responseTimeMs</c>) to stay v1-compatible for the load balancer and ops
/// tooling.
/// </summary>
public sealed record ServiceHealth(string Status, int? HttpStatus, long? ResponseTimeMs);

/// <summary>
/// The aggregated gateway health document. Shape is fixed for LB + ops
/// compatibility:
/// <c>{ gateway, timestamp, services: { [name]: { status, httpStatus, responseTimeMs } } }</c>.
/// The gateway itself always reports <c>"up"</c> here — a down service is
/// surfaced under <c>services</c> but never changes the gateway status or the
/// 200 response code (tech-spec §4.1).
/// </summary>
public sealed record AggregateHealthReport(
    string Gateway,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, ServiceHealth> Services);
