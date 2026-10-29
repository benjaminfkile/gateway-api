using System.Globalization;
using Amazon.RDS;
using Amazon.RDS.Model;
using Gateway.Api.Load;

namespace Gateway.Api.Resources;

/// <summary>
/// <see cref="IDatabaseScaler"/> over Amazon RDS (AWSSDK.RDS). The client comes from the
/// container: default credential chain, region from the environment. Connections in
/// use come from <see cref="IDatabaseHeadroom"/>, so this is registered scoped beside
/// it. An unknown instance reads as null; every other AWS error on the instance is
/// thrown. A parameter group that cannot be read leaves <c>MaxConnections</c> null.
/// </summary>
public sealed class RdsDatabaseScaler : IDatabaseScaler
{
    /// <summary>The Postgres parameter <see cref="DatabaseInstanceState.MaxConnections"/> reads.</summary>
    public const string MaxConnectionsParameter = "max_connections";

    private readonly IAmazonRDS _rds;
    private readonly IDatabaseHeadroom _headroom;
    private readonly ILogger<RdsDatabaseScaler> _logger;

    public RdsDatabaseScaler(IAmazonRDS rds, IDatabaseHeadroom headroom, ILogger<RdsDatabaseScaler> logger)
    {
        _rds = rds;
        _headroom = headroom;
        _logger = logger;
    }

    public async Task<DatabaseInstanceState?> DescribeInstanceAsync(string id, CancellationToken ct = default)
    {
        DBInstance? instance;
        try
        {
            var response = await _rds.DescribeDBInstancesAsync(new DescribeDBInstancesRequest
            {
                DBInstanceIdentifier = id,
            }, ct);
            instance = response.DBInstances?.FirstOrDefault();
        }
        catch (DBInstanceNotFoundException)
        {
            return null;
        }

        if (instance is null)
        {
            return null;
        }

        var parameterGroup = instance.DBParameterGroups?.FirstOrDefault()?.DBParameterGroupName;
        var maxConnections = parameterGroup is null ? null : await ReadMaxConnectionsAsync(parameterGroup, ct);
        var connections = await _headroom.GetAsync(ct);

        return new DatabaseInstanceState(
            instance.DBInstanceIdentifier,
            instance.DBInstanceClass,
            instance.DBInstanceStatus,
            maxConnections,
            connections?.InUse);
    }

    public Task ModifyInstanceClassAsync(string id, string instanceClass, CancellationToken ct = default) =>
        _rds.ModifyDBInstanceAsync(new ModifyDBInstanceRequest
        {
            DBInstanceIdentifier = id,
            DBInstanceClass = instanceClass,
            ApplyImmediately = true,
        }, ct);

    private async Task<int?> ReadMaxConnectionsAsync(string parameterGroup, CancellationToken ct)
    {
        try
        {
            string? marker = null;
            do
            {
                var page = await _rds.DescribeDBParametersAsync(new DescribeDBParametersRequest
                {
                    DBParameterGroupName = parameterGroup,
                    Marker = marker,
                }, ct);

                var parameter = page.Parameters?.FirstOrDefault(p =>
                    string.Equals(p.ParameterName, MaxConnectionsParameter, StringComparison.Ordinal));
                if (parameter is not null)
                {
                    // A plain number when an operator set it; the RDS default is a
                    // formula such as LEAST({DBInstanceClassMemory/9531392},5000).
                    return int.TryParse(parameter.ParameterValue, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                        ? value
                        : null;
                }

                marker = page.Marker;
            }
            while (!string.IsNullOrEmpty(marker));

            return null;
        }
        catch (AmazonRDSException ex)
        {
            _logger.LogWarning(ex, "Could not read {Parameter} from parameter group {Group}.", MaxConnectionsParameter, parameterGroup);
            return null;
        }
    }
}
