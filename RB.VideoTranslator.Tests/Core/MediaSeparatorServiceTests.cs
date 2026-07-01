using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using NSubstitute.ExceptionExtensions;
using RB.VideoTranslator.Core.Services;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Tests.Core;

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
        _fs.OpenRead(Arg.Any<string>()).Returns(_ => MakeWavHeaderStream());
        _sut = new MediaSeparatorService(
            _repo,
            NullLogger<MediaSeparatorService>.Instance,
            _processRunner,
            _fs);
    }

    // Minimal valid WAV header: 36 bytes covers all fields we probe (channels @22, rate @24).
    private static MemoryStream MakeWavHeaderStream(ushort channels = 2, uint sampleRate = 44100)
    {
        var header = new byte[36];
        "RIFF"u8.CopyTo(header);
        BitConverter.GetBytes(100u).CopyTo(header, 4);
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BitConverter.GetBytes(16u).CopyTo(header, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 20);       // PCM
        BitConverter.GetBytes(channels).CopyTo(header, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(header, 24);
        return new MemoryStream(header);
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

    // ── Audio format probing ───────────────────────────────────────────────────

    [Fact]
    public async Task SeparateAsync_SetsAudioChannelsFromWavHeader()
    {
        _fs.OpenRead(Arg.Any<string>()).Returns(_ => MakeWavHeaderStream(channels: 2));
        var job = MakeJob();
        await _sut.SeparateAsync(job);
        Assert.Equal(2, job.AudioChannels);
    }

    [Fact]
    public async Task SeparateAsync_DetectsMonoAudio()
    {
        _fs.OpenRead(Arg.Any<string>()).Returns(_ => MakeWavHeaderStream(channels: 1));
        var job = MakeJob();
        await _sut.SeparateAsync(job);
        Assert.Equal(1, job.AudioChannels);
    }

    [Fact]
    public async Task SeparateAsync_SetsSampleRateFromWavHeader()
    {
        _fs.OpenRead(Arg.Any<string>()).Returns(_ => MakeWavHeaderStream(sampleRate: 22050));
        var job = MakeJob();
        await _sut.SeparateAsync(job);
        Assert.Equal(22050, job.AudioSampleRate);
    }

    [Fact]
    public async Task SeparateAsync_DefaultSampleRateIs44100()
    {
        var job = MakeJob();
        await _sut.SeparateAsync(job);
        Assert.Equal(44100, job.AudioSampleRate);
    }
}
