using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RB.VideoTranslator.Core.Services;
using RB.VideoTranslator.Domain.Interfaces;
using Xunit;

namespace RB.VideoTranslator.Tests.Core;

public sealed class WhisperOnnxTranscriberServiceTests : IDisposable
{
    private readonly IProcessRunner _processRunner;
    private readonly WhisperOnnxTranscriberService _sut;
    private readonly string _npuExeDir = Path.Combine(AppContext.BaseDirectory, "npu-transcriber");
    private readonly string _npuExePath;
    private readonly string _audioDir;
    private readonly string _wavPath;

    public WhisperOnnxTranscriberServiceTests()
    {
        _processRunner = Substitute.For<IProcessRunner>();
        _sut = new WhisperOnnxTranscriberService(_processRunner, NullLogger<WhisperOnnxTranscriberService>.Instance);
        _npuExePath = Path.Combine(_npuExeDir, "RB.VideoTranslator.NpuTranscriber.exe");

        _audioDir = Path.Combine(Path.GetTempPath(), $"whisper_npu_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_audioDir);
        _wavPath = Path.Combine(_audioDir, "audio_16k.wav");
    }

    public void Dispose()
    {
        if (Directory.Exists(_npuExeDir))
            Directory.Delete(_npuExeDir, recursive: true);
        if (Directory.Exists(_audioDir))
            Directory.Delete(_audioDir, recursive: true);
    }

    [Fact]
    public async Task TryRunNpuExeAsync_ReturnsNullWithoutInvokingProcessRunner_WhenExeNotDeployed()
    {
        // The normal case in CI/test environments and any deployment that hasn't published
        // RB.VideoTranslator.NpuTranscriber alongside the app — must not even attempt to run it.
        Assert.False(File.Exists(_npuExePath));

        var result = await _sut.TryRunNpuExeAsync(_wavPath, "/models/whisper-medium", CancellationToken.None);

        Assert.Null(result);
        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRunNpuExeAsync_ReturnsNull_WhenProcessRunnerThrows()
    {
        // Simulates the real crash-containment scenario (observed on real hardware: the isolated
        // child process can segfault) without needing an actual native crash in the test — the
        // parent's IProcessRunner surfaces any abnormal child exit as an ordinary .NET exception
        // (see DefaultProcessRunner), which this method must swallow, not propagate.
        Directory.CreateDirectory(_npuExeDir);
        await File.WriteAllTextAsync(_npuExePath, "not a real exe, just needs to exist for File.Exists");

        _processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("exited with code -1073741819"));

        var result = await _sut.TryRunNpuExeAsync(_wavPath, "/models/whisper-medium", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRunNpuExeAsync_ReturnsNull_WhenProcessSucceedsButWritesNoOutput()
    {
        // The NPU exe exits 0 with "no compatible NPU" (its exit code 2 case) but produced no
        // JSON — must not throw trying to read a file that was never written.
        Directory.CreateDirectory(_npuExeDir);
        await File.WriteAllTextAsync(_npuExePath, "not a real exe, just needs to exist for File.Exists");

        _processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.TryRunNpuExeAsync(_wavPath, "/models/whisper-medium", CancellationToken.None);

        Assert.Null(result);
    }
}
