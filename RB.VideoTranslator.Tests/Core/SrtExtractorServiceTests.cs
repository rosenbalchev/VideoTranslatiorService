using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using RB.VideoTranslator.Core.Services;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Tests.Core;

public sealed class SrtExtractorServiceTests
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly SrtExtractorService _sut;

    public SrtExtractorServiceTests()
    {
        _repo = Substitute.For<IVideoJobRepository>();
        _processRunner = Substitute.For<IProcessRunner>();
        _fs = Substitute.For<IFileSystem>();
        _fs.FileExists(Arg.Any<string>()).Returns(true);
        _sut = new SrtExtractorService(
            _repo,
            _processRunner,
            _fs,
            NullLogger<SrtExtractorService>.Instance);
    }

    private static VideoJob MakeJob(string? extractedAudioPath = "/proc/video_audio.wav") => new()
    {
        OriginalFileName = "video.mp4",
        InputFilePath = "/input/video.mp4",
        ProcessingFolderPath = "/proc",
        ExtractedAudioPath = extractedAudioPath
    };

    [Fact]
    public async Task ExtractAsync_ThrowsWhenExtractedAudioPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob(extractedAudioPath: null)));
    }

    [Fact]
    public async Task ExtractAsync_ThrowsWhenExtractedAudioPathIsEmpty()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob(extractedAudioPath: "")));
    }

    [Fact]
    public async Task ExtractAsync_SetsSrtFilePath()
    {
        var job = MakeJob();

        await _sut.ExtractAsync(job);

        Assert.Equal(Path.Combine("/proc", "video.srt"), job.SrtFilePath);
    }

    [Fact]
    public async Task ExtractAsync_TransitionsStateToSrtExtracted()
    {
        var job = MakeJob();

        await _sut.ExtractAsync(job);

        await _repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.SrtExtracted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_InvokesPythonOnce()
    {
        await _sut.ExtractAsync(MakeJob(), "python");

        await _processRunner.Received(1)
            .RunAsync("python", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_ThrowsWhenSrtOutputFileMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.ExtractAsync(MakeJob()));
    }

    [Fact]
    public async Task ExtractAsync_DoesNotUpdateDbWhenSrtOutputFileMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        try { await _sut.ExtractAsync(MakeJob()); } catch (FileNotFoundException) { }

        await _repo.DidNotReceive().UpdateAsync(Arg.Any<VideoJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_PropagatesProcessRunnerException()
    {
        _processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("whisper exited with code 1."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob()));
    }
}
