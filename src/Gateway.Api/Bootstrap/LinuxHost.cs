using System.Diagnostics;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Production <see cref="ILinuxHost"/>: real file I/O and child processes on the
/// host the gateway runs on (tech-spec §4.3). Kept deliberately thin — it holds no
/// bootstrap logic, only the mechanics of reading/writing files and running
/// commands — so that all the interesting, testable behaviour lives in the
/// <see cref="IBootstrapStep"/> implementations against the interface.
/// </summary>
public sealed class LinuxHost : ILinuxHost
{
    public async Task<string?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, ct)
            : null;
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, ct);
    }

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
