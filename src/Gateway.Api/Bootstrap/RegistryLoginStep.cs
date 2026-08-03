using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Management;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Bootstrap step (tech-spec §4.3): log the Docker daemon in to the private image
/// registry so the reconciler can pull private images. Credentials come from
/// <see cref="IImageRegistry"/> (ECR in production) and are re-fetched on a timer by
/// <see cref="RegistryCredentialRefreshService"/> because they rotate.
/// <para>
/// Idempotency: the step persists a <b>fingerprint</b> (SHA-256) of the credentials
/// it last applied. If the current credentials hash to the same fingerprint it skips
/// the <c>docker login</c>; when they rotate, the fingerprint differs and it re-logs
/// in. The password is passed via stdin (<c>--password-stdin</c>) and never written
/// to disk or logs — only its fingerprint is stored.
/// </para>
/// </summary>
public sealed class RegistryLoginStep : IBootstrapStep
{
    private readonly ILinuxHost _host;
    private readonly IImageRegistry _registry;
    private readonly BootstrapOptions _options;
    private readonly ILogger<RegistryLoginStep> _logger;

    public RegistryLoginStep(
        ILinuxHost host,
        IImageRegistry registry,
        BootstrapOptions options,
        ILogger<RegistryLoginStep> logger)
    {
        _host = host;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    public string Name => "registry-login";

    public async Task<BootstrapStepResult> RunAsync(CancellationToken ct = default)
    {
        RegistryCredentials credentials;
        try
        {
            credentials = await _registry.GetCredentialsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No registry configured/reachable (e.g. no AWS on this box): nothing to
            // log in to. Not a failure of the box — just skip.
            _logger.LogWarning(ex, "No registry credentials available; skipping registry login.");
            return BootstrapStepResult.Skipped($"No registry credentials available: {ex.Message}");
        }

        var fingerprint = Fingerprint(credentials);
        var marker = (await _host.ReadFileAsync(_options.RegistryAuthStatePath, ct))?.Trim();
        if (string.Equals(marker, fingerprint, StringComparison.Ordinal))
        {
            return BootstrapStepResult.Skipped($"Registry '{credentials.Endpoint}' login already current.");
        }

        var login = await _host.RunAsync(
            "docker",
            new[] { "login", "--username", credentials.Username, "--password-stdin", credentials.Endpoint },
            credentials.Password,
            ct);

        if (!login.Succeeded)
        {
            throw new InvalidOperationException(
                $"docker login to '{credentials.Endpoint}' failed (exit {login.ExitCode}): {login.StandardError}");
        }

        await _host.WriteFileAsync(_options.RegistryAuthStatePath, fingerprint, ct);
        return BootstrapStepResult.Applied($"Logged in to registry '{credentials.Endpoint}'.");
    }

    /// <summary>Hash the credentials so a re-run can detect rotation without storing the secret.</summary>
    private static string Fingerprint(RegistryCredentials credentials)
    {
        var material = $"{credentials.Endpoint}\n{credentials.Username}\n{credentials.Password}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }
}
