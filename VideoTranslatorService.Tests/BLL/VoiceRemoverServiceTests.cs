using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using VideoTranslatorService.BLL.Services;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.Tests.BLL;

public sealed class VoiceRemoverServiceTests
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly VoiceRemoverService _sut;

    public VoiceRemoverServiceTests()
    {
        _repo = Substitute.For<IVideoJobRepository>();
        _processRunner = Substitute.For<IProcessRunner>();
        _fs = Substitute.For<IFileSystem>();
        _fs.FileExists(Arg.Any<string>()).Returns(true);
        // Default: CUDA is available
        _processRunner.RunAndCaptureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("True");
        _sut = new VoiceRemoverService(
            _repo,
            _processRunner,
            _fs,
            NullLogger<VoiceRemoverService>.Instance);
    }

    private static VideoJob MakeJob(string? extractedAudioPath = "/proc/video_audio.wav") => new()
    {
        OriginalFileName = "video.mp4",
        InputFilePath = "/input/video.mp4",
        ProcessingFolderPath = "/proc",
        ExtractedAudioPath = extractedAudioPath
    };

    [Fact]
    public async Task RemoveAsync_ThrowsWhenExtractedAudioPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RemoveAsync(MakeJob(extractedAudioPath: null)));
    }

    [Fact]
    public async Task RemoveAsync_ThrowsWhenExtractedAudioPathIsEmpty()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RemoveAsync(MakeJob(extractedAudioPath: "")));
    }

    [Fact]
    public async Task RemoveAsync_SetsVoiceRemovedAudioPathWithFlacExtension()
    {
        var job = MakeJob();

        await _sut.RemoveAsync(job);

        var expected = Path.Combine("/proc", "demucs", "htdemucs", "video_audio", "no_vocals.flac");
        Assert.Equal(expected, job.VoiceRemovedAudioPath);
    }

    [Fact]
    public async Task RemoveAsync_TransitionsStateToVoiceRemoved()
    {
        var job = MakeJob();

        await _sut.RemoveAsync(job);

        await _repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.VoiceRemoved),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_InvokesPythonOnce()
    {
        await _sut.RemoveAsync(MakeJob(), "python");

        await _processRunner.Received(1)
            .RunAsync("python", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_PassesDemucsModule()
    {
        await _sut.RemoveAsync(MakeJob(), "python");

        await _processRunner.Received(1)
            .RunAsync(
                "python",
                Arg.Is<string>(a => a.StartsWith("-m demucs ")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_PassesTwoStemsVocalsFlag()
    {
        await _sut.RemoveAsync(MakeJob(), "python");

        await _processRunner.Received(1)
            .RunAsync(
                "python",
                Arg.Is<string>(a => a.Contains("--two-stems=vocals")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_PassesFlacFlag()
    {
        await _sut.RemoveAsync(MakeJob(), "python");

        await _processRunner.Received(1)
            .RunAsync(
                "python",
                Arg.Is<string>(a => a.Contains("--flac")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_PropagatesProcessRunnerException()
    {
        _processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("demucs exited with code 1."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RemoveAsync(MakeJob()));
    }

    [Fact]
    public async Task RemoveAsync_ThrowsWhenOutputFlacFileMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.RemoveAsync(MakeJob()));
    }

    [Fact]
    public async Task RemoveAsync_DoesNotUpdateDbWhenOutputFileMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        try { await _sut.RemoveAsync(MakeJob()); } catch (FileNotFoundException) { }

        await _repo.DidNotReceive().UpdateAsync(Arg.Any<VideoJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_ThrowsWhenCudaNotAvailable()
    {
        _processRunner.RunAndCaptureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("False");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RemoveAsync(MakeJob()));
    }

    [Fact]
    public async Task RemoveAsync_DoesNotRunDemucsWhenCudaNotAvailable()
    {
        _processRunner.RunAndCaptureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("False");

        try { await _sut.RemoveAsync(MakeJob()); } catch (InvalidOperationException) { }

        await _processRunner.DidNotReceive()
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_PassesDeviceCudaFlag()
    {
        await _sut.RemoveAsync(MakeJob(), "python");

        await _processRunner.Received(1)
            .RunAsync(
                "python",
                Arg.Is<string>(a => a.Contains("--device cuda")),
                Arg.Any<CancellationToken>());
    }
}
