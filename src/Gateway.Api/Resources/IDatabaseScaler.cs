namespace Gateway.Api.Resources;

/// <summary>A database instance as <see cref="IDatabaseScaler.DescribeInstanceAsync"/> reads it.</summary>
/// <param name="Id">The instance identifier.</param>
/// <param name="InstanceClass">The instance class, e.g. <c>db.t4g.medium</c>.</param>
/// <param name="Status">The RDS status, e.g. <c>available</c> or <c>modifying</c>.</param>
/// <param name="MaxConnections">
/// <c>max_connections</c> from the instance's parameter group when it is a plain
/// number, else null (the RDS default is a formula over the instance memory).
/// </param>
/// <param name="ConnectionsInUse">
/// Connections in use as <see cref="Load.IDatabaseHeadroom"/> reads them on the
/// gateway's own database connection, or null when there is no database to ask.
/// </param>
public sealed record DatabaseInstanceState(string Id, string? InstanceClass, string? Status, int? MaxConnections, int? ConnectionsInUse);

/// <summary>
/// The RDS operations the Resources page needs for the database every manifest service
/// shares (docs/load.md section 9). Class only: the gateway never changes storage,
/// engine or anything else, and never creates or deletes an instance.
/// <para>
/// Behind an interface so the build/test box, which has no AWS access, substitutes a
/// fake. <see cref="RdsDatabaseScaler"/> is registered when
/// <c>Resources:DatabaseInstance</c> is set, <see cref="NullDatabaseScaler"/>
/// otherwise.
/// </para>
/// </summary>
public interface IDatabaseScaler
{
    /// <summary>The instance, or null when no such instance exists.</summary>
    Task<DatabaseInstanceState?> DescribeInstanceAsync(string id, CancellationToken ct = default);

    /// <summary>Change the instance class, applied immediately (the instance reboots).</summary>
    Task ModifyInstanceClassAsync(string id, string instanceClass, CancellationToken ct = default);
}
