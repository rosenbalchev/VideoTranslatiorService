using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

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

        if (!OperatingSystem.IsWindows())
        {
            var cudaLibraryPath = BuildLinuxCudaLibraryPath(executable);
            if (cudaLibraryPath is not null)
            {
                var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing)
                    ? cudaLibraryPath
                    : $"{cudaLibraryPath}{Path.PathSeparator}{existing}";
            }
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    // cuDNN 9's Linux wheel splits its backend into several .so files
    // (libcudnn_cnn.so.9, libcudnn_ops.so.9, ...) that libcudnn.so.9 itself
    // dlopen()s *by filename* the first time a matching op runs. That internal
    // dlopen() only finds them via LD_LIBRARY_PATH — and glibc snapshots that
    // variable once at process start, so setting it from inside the already-running
    // Python interpreter (as the Windows PATH-prepend equivalent does) has no
    // effect on Linux. It has to be set here, before the child process starts.
    // https://github.com/m-bain/whisperX/issues/1297
    internal static string? BuildLinuxCudaLibraryPath(string executable)
    {
        if (Path.GetFileName(executable) is not ("python" or "python3"))
            return null;

        // executable is "<venv>/bin/python" — site-packages sits at
        // "<venv>/lib/python3.XX/site-packages".
        var venvBin = Path.GetDirectoryName(executable);
        var venvRoot = venvBin is null ? null : Path.GetDirectoryName(venvBin);
        var libDir = venvRoot is null ? null : Path.Combine(venvRoot, "lib");
        if (libDir is null || !Directory.Exists(libDir))
            return null;

        var nvidiaDir = Directory.GetDirectories(libDir, "python3.*")
            .Select(d => Path.Combine(d, "site-packages", "nvidia"))
            .FirstOrDefault(Directory.Exists);
        if (nvidiaDir is null)
            return null;

        var nvidiaLibDirs = Directory.GetDirectories(nvidiaDir)
            .Select(d => Path.Combine(d, "lib"))
            .Where(Directory.Exists)
            .ToList();

        return nvidiaLibDirs.Count == 0 ? null : string.Join(Path.PathSeparator, nvidiaLibDirs);
    }
}
