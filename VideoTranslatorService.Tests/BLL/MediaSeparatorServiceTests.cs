using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using NSubstitute.ExceptionExtensions;
using VideoTranslatorService.BLL.Services;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.Tests.BLL;

public sealed class MediaSeparatorServiceTests
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly MediaSeparatorService _sut;

    public MediaSeparatorServiceTests()
    {
        _repo = Substitute.For<IVideoJobRepository>();
        _processRunner = Substitute.For<IProcessRunner>();
        _sut = new MediaSeparatorService(
            _repo,
            NullLogger<MediaSeparatorService>.Instance,
            _processRunner);
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

        Assert.Equal(Path.Combine("/proc", "video_audio.aac"), audioPath);
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

        Assert.Equal(Path.Combine("/proc", "video_audio.aac"), job.ExtractedAudioPath);
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
}
