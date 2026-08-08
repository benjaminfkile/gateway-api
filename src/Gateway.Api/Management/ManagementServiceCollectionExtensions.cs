using Amazon.CloudWatchLogs;
using Amazon.ECR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Management;

/// <summary>
/// Registration for the Management API's external-system seams (tech-spec §4.5):
/// the image registry (digest resolution), the centralized log store, and the
/// deploy-history store. Every external system is behind an interface so the
/// build/test box — no AWS, no Postgres — substitutes fakes.
/// <para>
/// The AWS-backed implementations are registered with lazy factories so a gateway
/// with no AWS region configured still boots; the clients are constructed only when
/// a deploy or logs request actually needs them. The deploy store defaults to a
/// no-op; <c>Program</c> registers the EF-backed store when a database is present.
/// <c>TryAdd</c> throughout so tests (and the DB branch) can substitute their own.
/// </para>
/// </summary>
public static class ManagementServiceCollectionExtensions
{
    public static IServiceCollection AddManagementServices(this IServiceCollection services)
    {
        // Deploy history: no-op unless Program wires the EF-backed store for a
        // configured database. Scoped to match the EF store's lifetime.
        services.TryAddScoped<IDeployStore, NullDeployStore>();

        // Image registry (ECR) — lazy so a region-less box boots; only constructed
        // when a deploy resolves a digest.
        services.TryAddSingleton<IAmazonECR>(_ => new AmazonECRClient());
        services.TryAddSingleton<IImageRegistry, EcrImageRegistry>();

        // Centralized log store (CloudWatch Logs) — lazy for the same reason.
        services.TryAddSingleton<IAmazonCloudWatchLogs>(_ => new AmazonCloudWatchLogsClient());
        services.TryAddSingleton<ILogStore, CloudWatchLogStore>();

        // Log-group retention admin (tech-spec §9): the reconciler sets 30-day
        // retention once per service group after a start that may have created it
        // (awslogs-create-group cannot set retention). Resolved on demand from the
        // reconcile loop, so the CloudWatch client is only built when it actually runs.
        services.TryAddSingleton<ILogGroupAdmin, CloudWatchLogGroupAdmin>();

        return services;
    }
}
