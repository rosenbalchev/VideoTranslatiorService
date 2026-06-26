using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using VideoTranslatorService.BLL.Services;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.Tests.BLL;

public sealed class SrtToAzureTtsServiceTests
{
    // ── WAV helpers ───────────────────────────────────────────────────────────

    // Minimal valid PCM WAV: standard 44-byte header + `pcmBytes` bytes of silence
    private static byte[] MakeWav(int pcmBytes = 4)
    {
        var wav = new byte[44 + pcmBytes];
        "RIFF"u8.CopyTo(wav);
        BitConverter.GetBytes((uint)(36 + pcmBytes)).CopyTo(wav, 4);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BitConverter.GetBytes(16u).CopyTo(wav, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(wav, 20);    // PCM
        BitConverter.GetBytes((ushort)1).CopyTo(wav, 22);    // mono
        BitConverter.GetBytes(44100u).CopyTo(wav, 24);
        BitConverter.GetBytes(88200u).CopyTo(wav, 28);
        BitConverter.GetBytes((ushort)2).CopyTo(wav, 32);
        BitConverter.GetBytes((ushort)16).CopyTo(wav, 34);
        "data"u8.CopyTo(wav.AsSpan(36));
        BitConverter.GetBytes((uint)pcmBytes).CopyTo(wav, 40);
        return wav;
    }

    // Engine returns a proper minimal WAV so ConcatenateWav doesn't throw when
    // the silence chunk is prepended to it.
    private static readonly byte[] FakeEngineWav = MakeWav(4);

    // ── SRT sample ────────────────────────────────────────────────────────────

    // Gap between entry 1 (ends 3 000 ms) and entry 2 (starts 5 000 ms) = 2 000 ms.
    // 2 000 ≤ MaxSilenceBreakMs (2 500), so both entries land in ONE segment.
    private const string SampleSrt = """
        1
        00:00:01,000 --> 00:00:03,000
        Hello world

        2
        00:00:05,000 --> 00:00:07,000
        Goodbye world
        """;

    // ── Test fixture ──────────────────────────────────────────────────────────

    private SrtToAzureTtsService MakeSut(
        out IVideoJobRepository repo,
        out IFileSystem fs,
        out IAzureSpeechEngine engine,
        string srtContent = SampleSrt)
    {
        repo   = Substitute.For<IVideoJobRepository>();
        fs     = Substitute.For<IFileSystem>();
        engine = Substitute.For<IAzureSpeechEngine>();

        fs.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(srtContent));
        fs.Create(Arg.Any<string>()).Returns(new MemoryStream());
        engine.SpeakSsmlAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FakeEngineWav));

        return new SrtToAzureTtsService(
            repo, fs, engine,
            NullLogger<SrtToAzureTtsService>.Instance);
    }

    private static VideoJob MakeJob(string? translatedSrtFilePath = "/proc/video_translated.srt") => new()
    {
        OriginalFileName      = "video.mp4",
        InputFilePath         = "/input/video.mp4",
        ProcessingFolderPath  = "/proc",
        SrtFilePath           = "/proc/video.srt",
        TranslatedSrtFilePath = translatedSrtFilePath
    };

    // ── Guard checks ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SynthesiseAsync_ThrowsWhenTranslatedSrtFilePathIsNull()
    {
        var sut = MakeSut(out _, out _, out _);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SynthesiseAsync(MakeJob(null), "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG"));
    }

    [Fact]
    public async Task SynthesiseAsync_ThrowsWhenTranslatedSrtFilePathIsEmpty()
    {
        var sut = MakeSut(out _, out _, out _);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SynthesiseAsync(MakeJob(""), "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG"));
    }

    [Fact]
    public async Task SynthesiseAsync_ThrowsWhenNoSubtitleEntries()
    {
        var sut = MakeSut(out _, out _, out _, srtContent: "not valid srt content");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SynthesiseAsync(MakeJob(), "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG"));
    }

    // ── SSML file ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SynthesiseAsync_WritesSsmlFileBeforeCallingEngine()
    {
        var sut = MakeSut(out _, out var fs, out var engine);
        var ssmlWritten = false;

        fs.WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => { ssmlWritten = true; return Task.CompletedTask; });
        engine.SpeakSsmlAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Assert.True(ssmlWritten, "SSML must be written before the engine is called");
                return Task.FromResult(FakeEngineWav);
            });

        await sut.SynthesiseAsync(MakeJob(), "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG");
    }

    [Fact]
    public async Task SynthesiseAsync_SsmlFileNameMatchesJobBaseName()
    {
        var sut = MakeSut(out _, out var fs, out _);
        await sut.SynthesiseAsync(MakeJob(), "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG");
        await fs.Received(1).WriteAllTextAsync(
            Arg.Is<string>(p => p.EndsWith("video_translated_azure_tts.ssml")),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynthesiseAsync_SsmlFileContainsAllEntries()
    {
        var sut = MakeSut(out _, out var fs, out _);
        string? ssml = null;
        await fs.WriteAllTextAsync(Arg.Any<string>(), Arg.Do<string>(s => ssml = s), Arg.Any<CancellationToken>());

        await sut.SynthesiseAsync(MakeJob(), "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG");

        Assert.NotNull(ssml);
        Assert.Contains("Hello world", ssml);
        Assert.Contains("Goodbye world", ssml);
    }

    // ── State transitions ─────────────────────────────────────────────────────

    [Fact]
    public async Task SynthesiseAsync_SetsAzureTtsAudioPath()
    {
        var sut = MakeSut(out _, out _, out _);
        var job = MakeJob();
        await sut.SynthesiseAsync(job, "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG");
        Assert.Equal(Path.Combine("/proc", "video_translated_azure_tts.wav"), job.AzureTtsAudioPath);
    }

    [Fact]
    public async Task SynthesiseAsync_TransitionsStateToAzureTtsSynthesised()
    {
        var sut = MakeSut(out var repo, out _, out _);
        await sut.SynthesiseAsync(MakeJob(), "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG");
        await repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.AzureTtsSynthesised),
            Arg.Any<CancellationToken>());
    }

    // ── Engine call verification ──────────────────────────────────────────────

    [Fact]
    public async Task SynthesiseAsync_PassesSubscriptionKeyToEngine()
    {
        var sut = MakeSut(out _, out _, out var engine);
        await sut.SynthesiseAsync(MakeJob(), "my-secret-key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG");
        await engine.Received().SpeakSsmlAsync(
            Arg.Any<string>(), Arg.Any<string>(), "my-secret-key",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynthesiseAsync_PassesEndpointUrlToEngine()
    {
        var sut = MakeSut(out _, out _, out var engine);
        await sut.SynthesiseAsync(MakeJob(), "key", "https://my-resource.cognitiveservices.azure.com/", "bg-BG-BorislavNeural", "bg-BG");
        await engine.Received().SpeakSsmlAsync(
            Arg.Any<string>(), "https://my-resource.cognitiveservices.azure.com/",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynthesiseAsync_PassesVoiceNameToEngine()
    {
        var sut = MakeSut(out _, out _, out var engine);
        await sut.SynthesiseAsync(MakeJob(), "key", "https://ep/", "en-US-Ava:DragonHDLatestNeural", "en-US");
        await engine.Received().SpeakSsmlAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            "en-US-Ava:DragonHDLatestNeural", Arg.Any<CancellationToken>());
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task SynthesiseAsync_ThrowsWhenEngineFails()
    {
        var sut = MakeSut(out _, out _, out var engine);
        engine.SpeakSsmlAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Azure TTS cancelled"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SynthesiseAsync(MakeJob(), "key", "https://ep/", "bg-BG-BorislavNeural", "bg-BG"));
    }

    // ── SplitIntoSegments unit tests ──────────────────────────────────────────

    [Fact]
    public void SplitIntoSegments_SingleEntryProducesOneSegment()
    {
        var entries = new List<SrtEntry> { new(0, 1000, "A") };
        var segs = SrtToAzureTtsService.SplitIntoSegments(entries, maxGapMs: 2500);
        Assert.Single(segs);
        Assert.Single(segs[0]);
    }

    [Fact]
    public void SplitIntoSegments_SmallGapKeepsEntriesTogether()
    {
        var entries = new List<SrtEntry>
        {
            new(0, 1000, "A"),
            new(1500, 2500, "B"), // gap = 500ms < 2500ms
        };
        var segs = SrtToAzureTtsService.SplitIntoSegments(entries, maxGapMs: 2500);
        Assert.Single(segs);
        Assert.Equal(2, segs[0].Count);
    }

    [Fact]
    public void SplitIntoSegments_LargeGapSplitsEntries()
    {
        var entries = new List<SrtEntry>
        {
            new(0, 1000, "A"),
            new(5000, 6000, "B"), // gap = 4000ms > 2500ms
        };
        var segs = SrtToAzureTtsService.SplitIntoSegments(entries, maxGapMs: 2500);
        Assert.Equal(2, segs.Count);
        Assert.Single(segs[0]);
        Assert.Single(segs[1]);
    }

    [Fact]
    public void SplitIntoSegments_MaxEntriesCapSplitsSegment()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => new SrtEntry(i * 100, i * 100 + 50, $"E{i}"))
            .ToList();
        var segs = SrtToAzureTtsService.SplitIntoSegments(entries, maxGapMs: 2500, maxEntries: 4);
        Assert.All(segs, s => Assert.True(s.Count <= 4));
        Assert.Equal(10, segs.Sum(s => s.Count));
    }

    [Fact]
    public void SplitIntoSegments_EmptyInputReturnsEmptyList()
    {
        var segs = SrtToAzureTtsService.SplitIntoSegments([], maxGapMs: 2500);
        Assert.Empty(segs);
    }

    // ── MakeSilenceWav unit tests ─────────────────────────────────────────────

    [Fact]
    public void MakeSilenceWav_ProducesCorrectByteLength()
    {
        // 1000ms @ 44100Hz 16-bit mono = 44100 * 2 = 88200 bytes PCM → 88244 total
        var wav = SrtToAzureTtsService.MakeSilenceWav(1000);
        Assert.Equal(44 + 44100 * 2, wav.Length);
    }

    [Fact]
    public void MakeSilenceWav_StartsWithRiffHeader()
    {
        var wav = SrtToAzureTtsService.MakeSilenceWav(100);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
    }

    [Fact]
    public void MakeSilenceWav_DataChunkSizeMatchesDuration()
    {
        var wav = SrtToAzureTtsService.MakeSilenceWav(500);
        var dataSize = BitConverter.ToUInt32(wav, 40);
        Assert.Equal((uint)(44100 / 2 * 2), dataSize); // 500ms → 22050 samples × 2 bytes
    }

    // ── ParseSrt unit tests ───────────────────────────────────────────────────

    [Fact]
    public void ParseSrt_ParsesTwoEntries()
    {
        var entries = SrtToAzureTtsService.ParseSrt(SampleSrt);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void ParseSrt_ParsesStartAndEndMs()
    {
        var entries = SrtToAzureTtsService.ParseSrt(SampleSrt);
        Assert.Equal(1_000, entries[0].StartMs);
        Assert.Equal(3_000, entries[0].EndMs);
    }

    [Fact]
    public void ParseSrt_ParsesText()
    {
        var entries = SrtToAzureTtsService.ParseSrt(SampleSrt);
        Assert.Equal("Hello world", entries[0].Text);
    }

    [Fact]
    public void ParseSrt_StripsHtmlTags()
    {
        const string srt = """
            1
            00:00:01,000 --> 00:00:03,000
            <i>Italic text</i>
            """;
        Assert.Equal("Italic text", SrtToAzureTtsService.ParseSrt(srt)[0].Text);
    }

    [Fact]
    public void ParseSrt_ReturnsEmptyListForInvalidInput()
    {
        Assert.Empty(SrtToAzureTtsService.ParseSrt("nothing valid here"));
    }

    [Fact]
    public void ParseSrt_HandlesWindowsLineEndings()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:03,000\r\nHello\r\n\r\n";
        var entries = SrtToAzureTtsService.ParseSrt(srt);
        Assert.Single(entries);
        Assert.Equal("Hello", entries[0].Text);
    }

    // ── BuildSsml unit tests ──────────────────────────────────────────────────

    [Fact]
    public void BuildSsml_ContainsVoiceName()
    {
        var entries = new List<SrtEntry> { new(1000, 3000, "Hello") };
        Assert.Contains("en-US-Ava:DragonHDLatestNeural",
            SrtToAzureTtsService.BuildSsml(entries, "en-US-Ava:DragonHDLatestNeural", "en-US"));
    }

    [Fact]
    public void BuildSsml_InsertsInitialBreakForFirstEntry()
    {
        var entries = new List<SrtEntry> { new(2000, 4000, "Hello") };
        Assert.Contains("<break time=\"2000ms\"/>",
            SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US"));
    }

    [Fact]
    public void BuildSsml_InsertsGapBreakBetweenEntries()
    {
        var entries = new List<SrtEntry> { new(0, 2000, "First"), new(5000, 7000, "Second") };
        // gap = 5000 - 2000 = 3000 ms
        Assert.Contains("<break time=\"3000ms\"/>",
            SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US"));
    }

    [Fact]
    public void BuildSsml_OmitsBreakWhenNoGap()
    {
        var entries = new List<SrtEntry> { new(0, 2000, "A"), new(2000, 4000, "B") };
        Assert.DoesNotContain("<break",
            SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US"));
    }

    [Fact]
    public void BuildSsml_StartOffsetReducesLeadingBreak()
    {
        var entries = new List<SrtEntry> { new(5000, 7000, "Hello") };
        var ssml = SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US", startOffsetMs: 3000);
        Assert.Contains("<break time=\"2000ms\"/>", ssml);
        Assert.DoesNotContain("5000ms", ssml);
    }

    [Fact]
    public void BuildSsml_StartOffsetEliminatesLeadingBreakWhenAligned()
    {
        var entries = new List<SrtEntry> { new(3000, 5000, "Hello") };
        Assert.DoesNotContain("<break",
            SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US", startOffsetMs: 3000));
    }

    [Fact]
    public void BuildSsml_EscapesAmpersand()
    {
        var entries = new List<SrtEntry> { new(0, 2000, "Rock & Roll") };
        Assert.Contains("Rock &amp; Roll",
            SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US"));
    }

    [Fact]
    public void BuildSsml_EscapesAngleBrackets()
    {
        var entries = new List<SrtEntry> { new(0, 2000, "a < b > c") };
        Assert.Contains("a &lt; b &gt; c",
            SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US"));
    }

    [Fact]
    public void BuildSsml_IsValidXmlDeclaration()
    {
        var entries = new List<SrtEntry> { new(500, 2000, "Text") };
        Assert.StartsWith("<?xml",
            SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US"));
    }

    // ── SpeechRateFor unit tests ──────────────────────────────────────────────

    [Fact]
    public void SpeechRateFor_ClampsOverflowToMaxRate()
    {
        // Long sentence, tight window → rate clamped to MaxRatePct (never exceed natural speed)
        var longText = new string('x', 200); // 200 chars at 13 c/s = ~15 384 ms natural
        var rate = SrtToAzureTtsService.SpeechRateFor(longText, availableMs: 5000);
        Assert.Equal(SrtToAzureTtsService.MaxRatePct, rate);
    }

    [Fact]
    public void SpeechRateFor_ReturnsDefaultRateWhenTextIsShortForWindow()
    {
        // Short word, generous window → DefaultRatePct — silence padding fills the rest
        var rate = SrtToAzureTtsService.SpeechRateFor("Hi", availableMs: 5000);
        Assert.Equal(SrtToAzureTtsService.DefaultRatePct, rate);
    }

    [Fact]
    public void SpeechRateFor_ClampsToMaxRate()
    {
        var rate = SrtToAzureTtsService.SpeechRateFor(new string('x', 1000), availableMs: 100);
        Assert.Equal(SrtToAzureTtsService.MaxRatePct, rate);
    }

    [Fact]
    public void SpeechRateFor_ClampsToDefaultRate()
    {
        // Even for very short text in a very wide window, we never go below DefaultRatePct.
        var rate = SrtToAzureTtsService.SpeechRateFor("Hi", availableMs: 60_000);
        Assert.Equal(SrtToAzureTtsService.DefaultRatePct, rate);
    }

    [Fact]
    public void SpeechRateFor_ReturnsDefaultRateForEmptyText()
    {
        Assert.Equal(SrtToAzureTtsService.DefaultRatePct, SrtToAzureTtsService.SpeechRateFor("", availableMs: 2000));
    }

    [Fact]
    public void SpeechRateFor_ReturnsDefaultRateForZeroWindow()
    {
        Assert.Equal(SrtToAzureTtsService.DefaultRatePct, SrtToAzureTtsService.SpeechRateFor("Hello", availableMs: 0));
    }

    [Fact]
    public void BuildSsml_OmitsProsodyTagWhenRateIsMaxAndMaxIsNatural()
    {
        // Very long text in a short window → rate clamped to MaxRatePct (100%) →
        // prosody tag is omitted because 100% is the voice's natural speed.
        var longText = new string('x', 500);
        var entries  = new List<SrtEntry> { new(0, 1000, longText) };
        var ssml     = SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US");
        Assert.DoesNotContain("<prosody", ssml);
    }

    [Fact]
    public void BuildSsml_OmitsProsodyTagWhenRateIsNearNormal()
    {
        // ~13 chars in 1000ms → estimated 1000ms → rate ≈ 100% → no prosody tag
        var text    = new string('x', 13); // 13 chars / 13 c/s = 1000ms
        var entries = new List<SrtEntry> { new(0, 1000, text) };
        var ssml    = SrtToAzureTtsService.BuildSsml(entries, "Voice", "en-US");
        Assert.DoesNotContain("<prosody", ssml);
    }

    // ── ConcatenateWav unit tests ─────────────────────────────────────────────

    [Fact]
    public void ConcatenateWav_SingleChunkReturnedUnchanged()
    {
        var wav = MakeWav(8);
        Assert.Same(wav, SrtToAzureTtsService.ConcatenateWav([wav]));
    }

    [Fact]
    public void ConcatenateWav_TwoChunksTotalSizeIsCorrect()
    {
        var result = SrtToAzureTtsService.ConcatenateWav([MakeWav(4), MakeWav(8)]);
        Assert.Equal(44 + 4 + 8, result.Length);
    }

    [Fact]
    public void ConcatenateWav_RiffSizeFieldIsPatched()
    {
        var result = SrtToAzureTtsService.ConcatenateWav([MakeWav(4), MakeWav(4)]);
        Assert.Equal((uint)(result.Length - 8), BitConverter.ToUInt32(result, 4));
    }

    [Fact]
    public void ConcatenateWav_DataSizeFieldIsPatched()
    {
        var result = SrtToAzureTtsService.ConcatenateWav([MakeWav(4), MakeWav(6)]);
        Assert.Equal(10u, BitConverter.ToUInt32(result, 40)); // 4 + 6 PCM bytes
    }
}
