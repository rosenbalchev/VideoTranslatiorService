using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using VideoTranslatorService.BLL.Services;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.Tests.BLL;

public sealed class VideoMuxerServiceTests
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _runner;
    private readonly IFileSystem _fs;
    private readonly VideoMuxerService _sut;

    public VideoMuxerServiceTests()
    {
        _repo   = Substitute.For<IVideoJobRepository>();
        _runner = Substitute.For<IProcessRunner>();
        _fs     = Substitute.For<IFileSystem>();

        _fs.FileExists(Arg.Any<string>()).Returns(true);

        _sut = new VideoMuxerService(
            _repo, _runner, _fs,
            NullLogger<VideoMuxerService>.Instance);
    }

    private static VideoJob MakeJob(
        string? silentVideoPath      = "/proc/video_silent.mp4",
        string? mixedAudioPath       = "/proc/video_mixed.wav",
        string? translatedSrtPath    = "/proc/video_translated_Bulgarian.srt",
        string  originalFileName     = "video.mp4") => new()
    {
        OriginalFileName      = originalFileName,
        InputFilePath         = "/input/video.mp4",
        ProcessingFolderPath  = "/proc",
        SilentVideoPath       = silentVideoPath,
        MixedAudioPath        = mixedAudioPath,
        TranslatedSrtFilePath = translatedSrtPath,
    };

    // ── Guard checks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MuxAsync_ThrowsWhenSilentVideoPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MuxAsync(MakeJob(silentVideoPath: null), "ffmpeg", "/out"));
    }

    [Fact]
    public async Task MuxAsync_ThrowsWhenMixedAudioPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MuxAsync(MakeJob(mixedAudioPath: null), "ffmpeg", "/out"));
    }

    [Fact]
    public async Task MuxAsync_ThrowsWhenTranslatedSrtPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MuxAsync(MakeJob(translatedSrtPath: null), "ffmpeg", "/out"));
    }

    // ── ffmpeg invocation ─────────────────────────────────────────────────────

    [Fact]
    public async Task MuxAsync_CallsProcessRunnerWithFfmpegPath()
    {
        await _sut.MuxAsync(MakeJob(), "custom-ffmpeg", "/out");
        await _runner.Received(1).RunAsync("custom-ffmpeg", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainAllThreeInputs()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out");
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a =>
                a.Contains("video_silent.mp4") &&
                a.Contains("video_mixed.wav") &&
                a.Contains("video_translated_Bulgarian.srt")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainSubtitleMap()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out");
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-map 2:s:0")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_UsesMp4SubtitleCodecForMp4Output()
    {
        await _sut.MuxAsync(MakeJob(originalFileName: "video.mp4"), "ffmpeg", "/out");
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("mov_text")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_UsesMkvSubtitleCodecForMkvOutput()
    {
        await _sut.MuxAsync(MakeJob(originalFileName: "video.mkv"), "ffmpeg", "/out");
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-c:s srt")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_OutputIsInOutputFolder()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/output");
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains(Path.Combine("/output", "video.mp4"))),
            Arg.Any<CancellationToken>());
    }

    // ── Output file ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MuxAsync_ThrowsFileNotFoundWhenOutputMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.MuxAsync(MakeJob(), "ffmpeg", "/out"));
    }

    [Fact]
    public async Task MuxAsync_DoesNotUpdateDbWhenOutputMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);
        try { await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out"); } catch { }
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<VideoJob>(), Arg.Any<CancellationToken>());
    }

    // ── State transitions ─────────────────────────────────────────────────────

    [Fact]
    public async Task MuxAsync_SetsOutputFilePath()
    {
        var job = MakeJob();
        await _sut.MuxAsync(job, "ffmpeg", "/output");
        Assert.Equal(Path.Combine("/output", "video.mp4"), job.OutputFilePath);
    }

    [Fact]
    public async Task MuxAsync_TransitionsStateToAddedToOriginalVideo()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out");
        await _repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.AddedToOriginalVideo),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ThrowsWhenProcessRunnerFails()
    {
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new InvalidOperationException("ffmpeg error"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MuxAsync(MakeJob(), "ffmpeg", "/out"));
    }
}
