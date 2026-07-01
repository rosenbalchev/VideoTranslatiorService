using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using RB.VideoTranslator.Core.Services;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Tests.Core;

public sealed class AudioMixerServiceTests
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _runner;
    private readonly IFileSystem _fs;
    private readonly AudioMixerService _sut;

    public AudioMixerServiceTests()
    {
        _repo   = Substitute.For<IVideoJobRepository>();
        _runner = Substitute.For<IProcessRunner>();
        _fs     = Substitute.For<IFileSystem>();

        _fs.FileExists(Arg.Any<string>()).Returns(true);

        _sut = new AudioMixerService(
            _repo, _runner, _fs,
            NullLogger<AudioMixerService>.Instance);
    }

    private static VideoJob MakeJob(
        string? voiceRemovedPath  = "/proc/no_vocals.flac",
        string? ttsPath           = "/proc/video_azure_tts.wav",
        string? translatedSrtPath = "/proc/video_translated_Bulgarian.srt") => new()
    {
        OriginalFileName      = "video.mp4",
        InputFilePath         = "/input/video.mp4",
        ProcessingFolderPath  = "/proc",
        VoiceRemovedAudioPath = voiceRemovedPath,
        AzureTtsAudioPath     = ttsPath,
        TranslatedSrtFilePath = translatedSrtPath,
    };

    // ── Guard checks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MixAsync_ThrowsWhenVoiceRemovedPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MixAsync(MakeJob(voiceRemovedPath: null)));
    }

    [Fact]
    public async Task MixAsync_ThrowsWhenAzureTtsPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MixAsync(MakeJob(ttsPath: null)));
    }

    // ── ffmpeg invocation ─────────────────────────────────────────────────────

    [Fact]
    public async Task MixAsync_CallsProcessRunnerWithFfmpegPath()
    {
        await _sut.MixAsync(MakeJob(), "my-ffmpeg");
        await _runner.Received(1).RunAsync("my-ffmpeg", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixAsync_ArgumentsContainBothInputFiles()
    {
        await _sut.MixAsync(MakeJob());
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("no_vocals.flac") && a.Contains("video_azure_tts.wav")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixAsync_ArgumentsContainAmixFilter()
    {
        await _sut.MixAsync(MakeJob());
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("amix")),
            Arg.Any<CancellationToken>());
    }

    // ── Output file ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MixAsync_OutputPathEndsWithMixedWav()
    {
        await _sut.MixAsync(MakeJob());
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("video_translated_Bulgarian_mixed.wav")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixAsync_ThrowsFileNotFoundWhenOutputMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.MixAsync(MakeJob()));
    }

    [Fact]
    public async Task MixAsync_DoesNotUpdateDbWhenOutputMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);
        try { await _sut.MixAsync(MakeJob()); } catch { }
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<VideoJob>(), Arg.Any<CancellationToken>());
    }

    // ── State transitions ─────────────────────────────────────────────────────

    [Fact]
    public async Task MixAsync_SetsMixedAudioPath()
    {
        var job = MakeJob();
        await _sut.MixAsync(job);
        Assert.Contains("video_translated_Bulgarian_mixed.wav", job.MixedAudioPath);
    }

    [Fact]
    public async Task MixAsync_TransitionsStateToMixedNoVoiceWithSyntheticVoice()
    {
        await _sut.MixAsync(MakeJob());
        await _repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.MixedNoVoiceWithSyntheticVoice),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixAsync_ThrowsWhenProcessRunnerFails()
    {
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new InvalidOperationException("ffmpeg error"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.MixAsync(MakeJob()));
    }

    // ── Channel-aware -ac flag ────────────────────────────────────────────────

    [Fact]
    public async Task MixAsync_UsesStereoAcForStereoInput()
    {
        var job = MakeJob();
        job.AudioChannels = 2;
        await _sut.MixAsync(job);
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-ac 2")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixAsync_UsesMonoAcForMonoInput()
    {
        var job = MakeJob();
        job.AudioChannels = 1;
        await _sut.MixAsync(job);
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-ac 1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixAsync_CapsAcAtStereoForSurroundInput()
    {
        // 5.1 surround source — dubbed track is capped at stereo
        var job = MakeJob();
        job.AudioChannels = 6;
        await _sut.MixAsync(job);
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-ac 2")),
            Arg.Any<CancellationToken>());
    }
}
