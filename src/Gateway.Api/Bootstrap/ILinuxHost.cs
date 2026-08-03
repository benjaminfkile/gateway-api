namespace Gateway.Api.Bootstrap;

/// <summary>
/// The single seam through which every node-bootstrap step touches the host
/// (tech-spec §4.3). All filesystem and process operations go through this
/// abstraction so the bootstrap pipeline is unit-testable against a fake host —
/// the build/test box has no root filesystem to mutate, no Docker daemon, and no
/// AWS access. The production implementation is <see cref="LinuxHost"/> over
/// <see cref="System.IO"/> and <see cref="System.Diagnostics.Process"/>.
/// </summary>
public interface ILinuxHost
{
    /// <summary>
    /// Read the text of a file, or <c>null</c> when it does not exist. Steps use
    /// this to compare desired vs. on-disk config so they can skip an unchanged
    /// write (idempotency).
    /// </summary>
    Task<string?> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Write text to a file, creating any missing parent directories. Overwrites
    /// existing content.
    /// </summary>
    Task WriteFileAsync(string path, string content, CancellationToken ct = default);

    /// <summary>
    /// Run a process and wait for it to exit, returning its exit code and captured
    /// output. Arguments are passed as a list (never shell-interpolated). Optional
    /// <paramref name="standardInput"/> is written to the process's stdin — used to
    /// pass a registry password to <c>docker login --password-stdin</c> so the
    /// secret never appears in an argument list.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken ct = default);
}

/// <summary>Exit code and captured output of a finished process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Whether the process exited successfully (exit code 0).</summary>
    public bool Succeeded => ExitCode == 0;
}
