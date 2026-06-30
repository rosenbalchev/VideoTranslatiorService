using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RB.VideoTranslator.BLL.Services;

public sealed class DefaultProcessRunner : IProcessRunner
{
    private readonly ILogger<DefaultProcessRunner> _logger;

    public DefaultProcessRunner(ILogger<DefaultProcessRunner> logger) => _logger = logger;

    public Task RunAsync(string executable, string arguments, CancellationToken ct = default) =>
        RunCoreAsync(executable, arguments, ct);

    public async Task<string> RunAndCaptureAsync(string executable, string arguments, CancellationToken ct = default)
    {
        using var process = StartProcess(executable, arguments);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr  = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("{Executable} stderr:\n{Stderr}", executable, stderr);
            throw new InvalidOperationException(
                $"{executable} exited with code {process.ExitCode}. See log for details.");
        }

        return stdout;
    }

    private async Task RunCoreAsync(string executable, string arguments, CancellationToken ct)
    {
        using var process = StartProcess(executable, arguments);
        // Read stderr concurrently to prevent the buffer from blocking the process.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("{Executable} stderr:\n{Stderr}", executable, stderr);
            throw new InvalidOperationException(
                $"{executable} exited with code {process.ExitCode}. See log for details.");
        }
    }

    private static Process StartProcess(string executable, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Force Python (and any tool that honours this variable) to use UTF-8 for
        // stdin/stdout/stderr, overriding the Windows console codepage (cp1252 etc.).
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }
}
