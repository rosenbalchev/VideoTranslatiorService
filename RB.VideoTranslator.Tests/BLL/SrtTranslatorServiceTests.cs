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

    // Well-formed GPT response: [N] translated-text lines.
    private const string TranslatedResponse = "[1] Здравей свят\n[2] Довиждане свят";

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
            .Returns(Task.FromResult(TranslatedResponse));

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
            .Returns(Task.FromResult(TranslatedResponse));

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
            .Returns(Task.FromResult(TranslatedResponse));

        await _sut.TranslateAsync(MakeJob(), "German");

        Assert.NotNull(capturedSystem);
        Assert.Contains("German", capturedSystem);
    }

    [Fact]
    public async Task TranslateAsync_ChunksLargeFiles()
    {
        // 120 blocks → 3 chat calls (50 + 50 + 20)
        var bigSrt = string.Join("\n\n", Enumerable.Range(1, 120).Select(i =>
            $"{i}\n00:00:{i:D2},000 --> 00:00:{i:D2},500\nLine {i}"));

        _fs.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(bigSrt));
        _chat.CompleteChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("translated chunk")); // unparseable — fallback to source text

        await _sut.TranslateAsync(MakeJob(), "Bulgarian");

        await _chat.Received(3).CompleteChatAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Timestamp preservation (core drift fix) ───────────────────────────────

    [Fact]
    public async Task TranslateAsync_DoesNotSendTimestampsToGpt()
    {
        string? capturedUser = null;
        _chat.CompleteChatAsync(
                Arg.Any<string>(),
                Arg.Do<string>(u => capturedUser = u),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TranslatedResponse));

        await _sut.TranslateAsync(MakeJob(), "Bulgarian");

        Assert.NotNull(capturedUser);
        Assert.DoesNotContain("-->", capturedUser);
        Assert.Contains("[1]", capturedUser);
        Assert.Contains("[2]", capturedUser);
    }

    [Fact]
    public async Task TranslateAsync_PreservesOriginalTimestampsInOutput()
    {
        string? capturedOutput = null;
        _fs.WriteAllTextAsync(
                Arg.Any<string>(),
                Arg.Do<string>(s => capturedOutput = s),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.TranslateAsync(MakeJob(), "Bulgarian");

        Assert.NotNull(capturedOutput);
        Assert.Contains("00:00:01,000 --> 00:00:03,000", capturedOutput);
        Assert.Contains("00:00:05,000 --> 00:00:07,000", capturedOutput);
        Assert.Contains("Здравей свят", capturedOutput);
        Assert.Contains("Довиждане свят", capturedOutput);
    }

    [Fact]
    public async Task TranslateAsync_FallsBackToSourceTextWhenResponseUnparseable()
    {
        string? capturedOutput = null;
        _fs.WriteAllTextAsync(
                Arg.Any<string>(),
                Arg.Do<string>(s => capturedOutput = s),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _chat.CompleteChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("some garbage with no markers"));

        await _sut.TranslateAsync(MakeJob(), "Bulgarian");

        Assert.NotNull(capturedOutput);
        Assert.Contains("Hello world", capturedOutput);
        Assert.Contains("Goodbye world", capturedOutput);
        // Timestamps must still come from the original
        Assert.Contains("00:00:01,000 --> 00:00:03,000", capturedOutput);
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

    // ── ParseBlocks unit tests ─────────────────────────────────────────────────

    [Fact]
    public void ParseBlocks_ExtractsTimestampAndText()
    {
        var blocks = SrtTranslatorService.ParseBlocks(SampleSrt);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("00:00:01,000 --> 00:00:03,000", blocks[0].TimestampLine);
        Assert.Equal("Hello world", blocks[0].Text);
        Assert.Equal("00:00:05,000 --> 00:00:07,000", blocks[1].TimestampLine);
        Assert.Equal("Goodbye world", blocks[1].Text);
    }

    [Fact]
    public void ParseBlocks_JoinsMultiLineText()
    {
        const string srt = """
            1
            00:00:01,000 --> 00:00:03,000
            Line one
            Line two
            """;

        var blocks = SrtTranslatorService.ParseBlocks(srt);

        Assert.Single(blocks);
        Assert.Equal("Line one Line two", blocks[0].Text);
    }

    [Fact]
    public void ParseBlocks_StripsInlineMarkup()
    {
        const string srt = """
            1
            00:00:01,000 --> 00:00:03,000
            <i>Hello</i> world
            """;

        var blocks = SrtTranslatorService.ParseBlocks(srt);

        Assert.Single(blocks);
        Assert.Equal("Hello world", blocks[0].Text);
    }
}
