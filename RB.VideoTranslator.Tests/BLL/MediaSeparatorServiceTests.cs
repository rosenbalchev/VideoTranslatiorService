using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using NSubstitute.ExceptionExtensions;
using RB.VideoTranslator.BLL.Services;
using RB.VideoTranslator.Data.Entities;
using RB.VideoTranslator.Data.Repositories;

namespace RB.VideoTranslator.Tests.BLL;

public sealed class MediaSeparatorServiceTests
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly MediaSeparatorService _sut;

    public MediaSeparatorServiceTests()
    {
        _repo = Substitute.For<IVideoJobRepository>();
        _processRunner = Substitute.For<IProcessRunner>();
        _fs = Substitute.For<IFileSystem>();
        _fs.FileExists(Arg.Any<string>()).Returns(true);
        _sut = new MediaSeparatorService(
            _repo,
            NullLogger<MediaSeparatorService>.Instance,
            _processRunner,
            _fs);
    }

    private static VideoJob MakeJob(string? processingVideoPath = "/proc/video.mp4") => new()
    {
        OriginalFileName = "video.mp4",
        InputFilePath = "/input/video.mp4",
        ProcessingFolderPath = "/proc",
        ProcessingVideoPath = processingVideoPath
    };

    [Fact]
    public async Task SeparateAsync_ThrowsWhenProcessingVideoPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SeparateAsync(MakeJob(processingVideoPath: null)));
    }

    [Fact]
    public async Task SeparateAsync_ThrowsWhenProcessingVideoPathIsEmpty()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SeparateAsync(MakeJob(processingVideoPath: "")));
    }

    [Fact]
    public async Task SeparateAsync_ReturnsCorrectAudioPath()
    {
        var (audioPath, _) = await _sut.SeparateAsync(MakeJob());

        Assert.Equal(Path.Combine("/proc", "video_audio.wav"), audioPath);
    }

    [Fact]
    public async Task SeparateAsync_ReturnsCorrectSilentVideoPath()
    {
        var (_, silentVideoPath) = await _sut.SeparateAsync(MakeJob());

        Assert.Equal(Path.Combine("/proc", "video_silent.mp4"), silentVideoPath);
    }

    [Fact]
    public async Task SeparateAsync_UpdatesJobPaths()
    {
        var job = MakeJob();

        await _sut.SeparateAsync(job);

        Assert.Equal(Path.Combine("/proc", "video_audio.wav"), job.ExtractedAudioPath);
        Assert.Equal(Path.Combine("/proc", "video_silent.mp4"), job.SilentVideoPath);
    }

    [Fact]
    public async Task SeparateAsync_TransitionsStateToAudioExtracted()
    {
        var job = MakeJob();

        await _sut.SeparateAsync(job);

        await _repo.Received(1).UpdateAsync(Arg.Is<VideoJob>(j => j.State == JobState.AudioExtracted));
    }

    [Fact]
    public async Task SeparateAsync_InvokesFfmpegTwice()
    {
        await _sut.SeparateAsync(MakeJob(), "ffmpeg");

        await _processRunner.Received(2)
            .RunAsync("ffmpeg", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeparateAsync_PropagatesProcessRunnerException()
    {
        _processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ffmpeg exited with code 1."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SeparateAsync(MakeJob()));
    }

    [Fact]
    public async Task SeparateAsync_ThrowsWhenAudioOutputFileMissing()
    {
        var job = MakeJob();
        var expectedAudio = Path.Combine("/proc", "video_audio.wav");
        _fs.FileExists(expectedAudio).Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.SeparateAsync(job));
    }

    [Fact]
    public async Task SeparateAsync_ThrowsWhenSilentVideoOutputFileMissing()
    {
        var job = MakeJob();
        var expectedVideo = Path.Combine("/proc", "video_silent.mp4");
        _fs.FileExists(expectedVideo).Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.SeparateAsync(job));
    }

    [Fact]
    public async Task SeparateAsync_DoesNotUpdateDbWhenOutputFileMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        try { await _sut.SeparateAsync(MakeJob()); } catch (FileNotFoundException) { }

        await _repo.DidNotReceive().UpdateAsync(Arg.Any<VideoJob>(), Arg.Any<CancellationToken>());
    }
}
