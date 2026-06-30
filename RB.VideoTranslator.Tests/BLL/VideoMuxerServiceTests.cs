using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using RB.VideoTranslator.BLL.Services;
using RB.VideoTranslator.Data.Entities;
using RB.VideoTranslator.Data.Repositories;

namespace RB.VideoTranslator.Tests.BLL;

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
        string? silentVideoPath   = "/proc/video_silent.mp4",
        string? extractedAudioPath = "/proc/video.wav",
        string  originalFileName  = "video.mp4",
        string? srtFilePath       = "/proc/video.srt") => new()
    {
        OriginalFileName      = originalFileName,
        InputFilePath         = "/input/video.mp4",
        ProcessingFolderPath  = "/proc",
        SilentVideoPath       = silentVideoPath,
        ExtractedAudioPath    = extractedAudioPath,
        SrtFilePath           = srtFilePath,
    };

    private static IReadOnlyList<LanguageResult> OneLang(
        string lang           = "Bulgarian",
        string mixedAudioPath = "/proc/video_bg_mixed.wav",
        string srtPath        = "/proc/video_translated_Bulgarian.srt") =>
        [new LanguageResult(lang, mixedAudioPath, srtPath)];

    private static IReadOnlyList<LanguageResult> TwoLangs() =>
    [
        new LanguageResult("Bulgarian", "/proc/video_bg_mixed.wav", "/proc/video_bg.srt"),
        new LanguageResult("English",   "/proc/video_en_mixed.wav", "/proc/video_en.srt"),
    ];

    // ── Guard checks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MuxAsync_ThrowsWhenSilentVideoPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MuxAsync(MakeJob(silentVideoPath: null), "ffmpeg", "/out", OneLang()));
    }

    [Fact]
    public async Task MuxAsync_ThrowsWhenExtractedAudioPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MuxAsync(MakeJob(extractedAudioPath: null), "ffmpeg", "/out", OneLang()));
    }

    [Fact]
    public async Task MuxAsync_ThrowsWhenLanguageResultsEmpty()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", []));
    }

    [Fact]
    public async Task MuxAsync_ThrowsWhenSrtFilePathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MuxAsync(MakeJob(srtFilePath: null), "ffmpeg", "/out", OneLang()));
    }

    // ── ffmpeg invocation ─────────────────────────────────────────────────────

    [Fact]
    public async Task MuxAsync_CallsProcessRunnerWithFfmpegPath()
    {
        await _sut.MuxAsync(MakeJob(), "custom-ffmpeg", "/out", OneLang());
        // 1 multi-audio master + 1 per-language = 2 calls for OneLang()
        await _runner.Received(2).RunAsync("custom-ffmpeg", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainSilentVideoAndOriginalAudio()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang());
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("video_silent.mp4") && a.Contains("video.wav")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainLanguageMixedAudio()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("video_bg_mixed.wav")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainOriginalSrtPath()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("video.srt")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainAllLanguageAudioForTwoLangs()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", TwoLangs());
        await _runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("video_bg_mixed.wav") && a.Contains("video_en_mixed.wav")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainFilterComplexWithApad()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-filter_complex") && a.Contains("apad")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainShortest()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-shortest")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainOriginalMetadataTitle()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("title=\"Original\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_ArgumentsContainLanguageMetadataTitle()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang("Bulgarian"));
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("title=\"Bulgarian\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_OriginalSubtitleTrackHasOriginalMetadata()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-metadata:s:s:0 title=\"Original\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_UsesMp4SubtitleCodecForMp4Output()
    {
        await _sut.MuxAsync(MakeJob(originalFileName: "video.mp4"), "ffmpeg", "/out", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("mov_text")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_UsesMkvSubtitleCodecForMkvOutput()
    {
        await _sut.MuxAsync(MakeJob(originalFileName: "video.mkv"), "ffmpeg", "/out", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("-c:s srt")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MuxAsync_OutputIsInOutputFolder()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/output", OneLang());
        await _runner.Received().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains(Path.Combine("/output", "video_multiAudio.mp4"))),
            Arg.Any<CancellationToken>());
    }

    // ── BuildFfmpegArgs unit tests ────────────────────────────────────────────

    [Fact]
    public void BuildFfmpegArgs_TwoLanguagesHasTwoApadEntries()
    {
        var job = MakeJob();
        var args = VideoMuxerService.BuildFfmpegArgs(job, TwoLangs(), "mov_text", "/out/video.mp4");
        // filter_complex should contain apad for original + 2 languages = 3 streams
        Assert.Contains("[1:a]apad[a0]", args);
        Assert.Contains("[2:a]apad[a1]", args);
        Assert.Contains("[3:a]apad[a2]", args);
    }

    [Fact]
    public void BuildFfmpegArgs_SrtInputsAppearAfterAudioInputs()
    {
        var job  = MakeJob();
        var args = VideoMuxerService.BuildFfmpegArgs(job, TwoLangs(), "mov_text", "/out/video.mp4");
        // 1 video + 1 original audio + 2 lang audio = first SRT at input index 4
        // index 4: original SRT, 5: Bulgarian, 6: English
        Assert.Contains("-map 4:s:0", args); // original SRT
        Assert.Contains("-map 5:s:0", args); // Bulgarian
        Assert.Contains("-map 6:s:0", args); // English
    }

    // ── Output file ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MuxAsync_ThrowsFileNotFoundWhenOutputMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang()));
    }

    [Fact]
    public async Task MuxAsync_DoesNotUpdateDbWhenOutputMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);
        try { await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang()); } catch { }
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<VideoJob>(), Arg.Any<CancellationToken>());
    }

    // ── State transitions ─────────────────────────────────────────────────────

    [Fact]
    public async Task MuxAsync_SetsOutputFilePath()
    {
        var job = MakeJob();
        await _sut.MuxAsync(job, "ffmpeg", "/output", OneLang());
        Assert.Equal(Path.Combine("/output", "video_multiAudio.mp4"), job.OutputFilePath);
    }

    [Fact]
    public async Task MuxAsync_TransitionsStateToAddedToOriginalVideo()
    {
        await _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang());
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
            () => _sut.MuxAsync(MakeJob(), "ffmpeg", "/out", OneLang()));
    }
}
