using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VideoTranslatorService.BLL.Services;

public sealed class DefaultProcessRunner : IProcessRunner
{
    private readonly ILogger<DefaultProcessRunner> _logger;

    public DefaultProcessRunner(ILogger<DefaultProcessRunner> logger) => _logger = logger;

    public async Task RunAsync(string executable, string arguments, CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

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
}
