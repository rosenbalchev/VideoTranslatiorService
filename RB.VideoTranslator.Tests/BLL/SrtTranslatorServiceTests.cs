using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using RB.VideoTranslator.BLL.Services;
using RB.VideoTranslator.Data.Entities;
using RB.VideoTranslator.Data.Repositories;

namespace RB.VideoTranslator.Tests.BLL;

public sealed class SrtTranslatorServiceTests
{
    private const string SampleSrt = """
        1
        00:00:01,000 --> 00:00:03,000
        Hello world

        2
        00:00:05,000 --> 00:00:07,000
        Goodbye world
        """;

    private readonly IVideoJobRepository _repo;
    private readonly IFileSystem _fs;
    private readonly IAzureChatEngine _chat;
    private readonly SrtTranslatorService _sut;

    public SrtTranslatorServiceTests()
    {
        _repo = Substitute.For<IVideoJobRepository>();
        _fs   = Substitute.For<IFileSystem>();
        _chat = Substitute.For<IAzureChatEngine>();

        _fs.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleSrt));
        _chat.CompleteChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleSrt)); // echo back unchanged for simplicity

        _sut = new SrtTranslatorService(
            _repo, _fs, _chat,
            NullLogger<SrtTranslatorService>.Instance);
    }

    private static VideoJob MakeJob(string? srtFilePath = "/proc/video.srt") => new()
    {
        OriginalFileName     = "video.mp4",
        InputFilePath        = "/input/video.mp4",
        ProcessingFolderPath = "/proc",
        SrtFilePath          = srtFilePath
    };

    // ── Guard checks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_ThrowsWhenSrtFilePathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.TranslateAsync(MakeJob(null), "Bulgarian"));
    }

    [Fact]
    public async Task TranslateAsync_ThrowsWhenSrtFilePathIsEmpty()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.TranslateAsync(MakeJob(""), "Bulgarian"));
    }

    // ── Chat engine interaction ───────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_CallsChatEngine()
    {
        await _sut.TranslateAsync(MakeJob(), "Bulgarian");
        await _chat.Received().CompleteChatAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TranslateAsync_SystemPromptContainsTargetLanguage()
    {
        string? capturedSystem = null;
        _chat.CompleteChatAsync(
                Arg.Do<string>(s => capturedSystem = s),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleSrt));

        await _sut.TranslateAsync(MakeJob(), "Bulgarian");

        Assert.NotNull(capturedSystem);
        Assert.Contains("Bulgarian", capturedSystem);
    }

    [Fact]
    public async Task TranslateAsync_SystemPromptContainsDifferentTargetLanguage()
    {
        string? capturedSystem = null;
        _chat.CompleteChatAsync(
                Arg.Do<string>(s => capturedSystem = s),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleSrt));

        await _sut.TranslateAsync(MakeJob(), "German");

        Assert.NotNull(capturedSystem);
        Assert.Contains("German", capturedSystem);
    }

    [Fact]
    public async Task TranslateAsync_ChunksLargeFiles()
    {
        // Build an SRT with 120 blocks — should produce 3 chat calls (50 + 50 + 20)
        var bigSrt = string.Join("\n\n", Enumerable.Range(1, 120).Select(i =>
            $"{i}\n00:00:{i:D2},000 --> 00:00:{i:D2},500\nLine {i}"));

        _fs.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(bigSrt));
        _chat.CompleteChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("translated chunk"));

        await _sut.TranslateAsync(MakeJob(), "Bulgarian");

        await _chat.Received(3).CompleteChatAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Output file ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_WritesOutputFileWithCorrectName()
    {
        await _sut.TranslateAsync(MakeJob(), "Bulgarian");
        await _fs.Received(1).WriteAllTextAsync(
            Arg.Is<string>(p => p.EndsWith("video_translated_Bulgarian.srt")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TranslateAsync_OutputFileNameReflectsTargetLanguage()
    {
        await _sut.TranslateAsync(MakeJob(), "English");
        await _fs.Received(1).WriteAllTextAsync(
            Arg.Is<string>(p => p.EndsWith("video_translated_English.srt")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ── State transitions ─────────────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_SetsTranslatedSrtFilePath()
    {
        var job = MakeJob();
        await _sut.TranslateAsync(job, "Bulgarian");
        Assert.Contains("video_translated_Bulgarian.srt", job.TranslatedSrtFilePath);
    }

    [Fact]
    public async Task TranslateAsync_TransitionsStateToSrtTranslated()
    {
        await _sut.TranslateAsync(MakeJob(), "Bulgarian");
        await _repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.SrtTranslated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TranslateAsync_DoesNotUpdateDbWhenChatFails()
    {
        _chat.CompleteChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new HttpRequestException("Network error"));

        try { await _sut.TranslateAsync(MakeJob(), "Bulgarian"); } catch { }

        await _repo.DidNotReceive().UpdateAsync(Arg.Any<VideoJob>(), Arg.Any<CancellationToken>());
    }
}
