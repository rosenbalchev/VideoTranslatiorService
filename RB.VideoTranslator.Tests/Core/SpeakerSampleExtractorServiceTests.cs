using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using RB.VideoTranslator.Core.Services;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Tests.Core;

public sealed class SpeakerSampleExtractorServiceTests
{
    private const string SampleVttWithSummary = """
        WEBVTT

        NOTE
        The conversation contains 2 speakers.
        Speaker1|Female|00:00:00|00:00:26|232Hz
        Speaker2|Male|00:02:58|00:03:26|117Hz

        NOTE Speaker1 (estimated: female)

        1
        00:00:00.031 --> 00:00:26.490
        Hello there, this is the longest thing Speaker1 says.

        NOTE Speaker2 (estimated: male)

        2
        00:00:30.000 --> 00:00:31.000
        Short reply.

        NOTE Speaker2 (estimated: male)

        3
        00:02:58.000 --> 00:03:26.000
        This is Speaker2's longest utterance in the conversation.
        """;

    private const string SampleVttWithoutSummary = """
        WEBVTT

        1
        00:00:01.000 --> 00:00:03.000
        Hello world
        """;

    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly SpeakerSampleExtractorService _sut;

    public SpeakerSampleExtractorServiceTests()
    {
        _repo = Substitute.For<IVideoJobRepository>();
        _processRunner = Substitute.For<IProcessRunner>();
        _fs = Substitute.For<IFileSystem>();
        _fs.FileExists(Arg.Any<string>()).Returns(true);
        _fs.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleVttWithSummary));

        _sut = new SpeakerSampleExtractorService(
            _repo, _processRunner, _fs, NullLogger<SpeakerSampleExtractorService>.Instance);
    }

    private static VideoJob MakeJob(string? vocalsAudioPath = "/proc/demucs/htdemucs/video_audio/vocals.flac",
        string? vttFilePath = "/proc/video.vtt") => new()
    {
        OriginalFileName     = "video.mp4",
        InputFilePath        = "/input/video.mp4",
        ProcessingFolderPath = "/proc",
        VocalsAudioPath      = vocalsAudioPath,
        VttFilePath          = vttFilePath
    };

    // ── Guard checks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_ThrowsWhenVocalsAudioPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob(vocalsAudioPath: null)));
    }

    [Fact]
    public async Task ExtractAsync_ThrowsWhenVttFilePathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob(vttFilePath: null)));
    }

    // ── Extraction ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_RunsFfmpegOncePerSpeaker()
    {
        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        await _processRunner.Received(2).RunAsync(
            "ffmpeg", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_UsesVocalsTrackAsInput()
    {
        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        // Both speaker extractions read from the same isolated vocals track.
        await _processRunner.Received(2).RunAsync(
            "ffmpeg",
            Arg.Is<string>(a => a.Contains("vocals.flac")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_PassesSpeakerStartAndEndTimes()
    {
        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        await _processRunner.Received(1).RunAsync(
            "ffmpeg",
            Arg.Is<string>(a => a.Contains("-ss 00:00:00") && a.Contains("-to 00:00:26")),
            Arg.Any<CancellationToken>());

        await _processRunner.Received(1).RunAsync(
            "ffmpeg",
            Arg.Is<string>(a => a.Contains("-ss 00:02:58") && a.Contains("-to 00:03:26")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_WritesOneFilePerSpeakerLabel()
    {
        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        var expected1 = Path.Combine("/proc", "speaker_samples", "Speaker1.wav");
        var expected2 = Path.Combine("/proc", "speaker_samples", "Speaker2.wav");

        await _processRunner.Received(1).RunAsync(
            "ffmpeg", Arg.Is<string>(a => a.Contains(expected1)), Arg.Any<CancellationToken>());
        await _processRunner.Received(1).RunAsync(
            "ffmpeg", Arg.Is<string>(a => a.Contains(expected2)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_SetsSpeakerSamplePathsJson()
    {
        var job = MakeJob();

        await _sut.ExtractAsync(job, "ffmpeg");

        Assert.NotNull(job.SpeakerSamplePathsJson);
        Assert.Contains("Speaker1", job.SpeakerSamplePathsJson);
        Assert.Contains("Speaker2", job.SpeakerSamplePathsJson);
    }

    [Fact]
    public async Task ExtractAsync_TransitionsStateToSpeakerSamplesExtracted()
    {
        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        await _repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.SpeakerSamplesExtracted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_WritesTranscriptTxtFileForEachSpeaker()
    {
        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        var txt1 = Path.Combine("/proc", "speaker_samples", "Speaker1.txt");
        var txt2 = Path.Combine("/proc", "speaker_samples", "Speaker2.txt");

        await _fs.Received(1).WriteAllTextAsync(txt1, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fs.Received(1).WriteAllTextAsync(txt2, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_TxtFileContainsTextOfLongestCueNotShorterOnes()
    {
        var writtenTexts = new Dictionary<string, string>();
        _fs.WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => writtenTexts[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1));

        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        var txt1 = Path.Combine("/proc", "speaker_samples", "Speaker1.txt");
        var txt2 = Path.Combine("/proc", "speaker_samples", "Speaker2.txt");

        Assert.Equal("Hello there, this is the longest thing Speaker1 says.", writtenTexts[txt1]);
        // Speaker2 has two cues — the longer one (00:02:58 → 00:03:26) must win, not "Short reply."
        Assert.Equal("This is Speaker2's longest utterance in the conversation.", writtenTexts[txt2]);
    }

    [Fact]
    public async Task ExtractAsync_ThrowsWhenExpectedOutputFileMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.ExtractAsync(MakeJob(), "ffmpeg"));
    }

    // ── No speaker summary (diarization was skipped / --no-diarize) ──────────

    [Fact]
    public async Task ExtractAsync_SkipsExtractionWhenNoSpeakerSummaryPresent()
    {
        _fs.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleVttWithoutSummary));

        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_StillTransitionsStateWhenNoSpeakerSummaryPresent()
    {
        _fs.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleVttWithoutSummary));

        await _sut.ExtractAsync(MakeJob(), "ffmpeg");

        await _repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.SpeakerSamplesExtracted),
            Arg.Any<CancellationToken>());
    }

    // ── FindSampleCue unit tests ───────────────────────────────────────────────

    [Fact]
    public void FindSampleCue_PicksFirstCueOver4SecondsNotTheLongerLaterOne()
    {
        var cues = new List<SpeakerSampleExtractorService.SpeakerCue>
        {
            new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1.2), "too short 1"),
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4.0), "too short 2"),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10.5), "first over 4s"),
            new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(28.1), "longer but later"),
        };

        var picked = SpeakerSampleExtractorService.FindSampleCue(cues);

        Assert.NotNull(picked);
        Assert.Equal("first over 4s", picked.Value.Text);
    }

    [Fact]
    public void FindSampleCue_FallsBackToLongestWhenNoneExceedThreshold()
    {
        var cues = new List<SpeakerSampleExtractorService.SpeakerCue>
        {
            new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2.0), "longest short one"),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.5), "shorter"),
        };

        var picked = SpeakerSampleExtractorService.FindSampleCue(cues);

        Assert.NotNull(picked);
        Assert.Equal("longest short one", picked.Value.Text);
    }

    [Fact]
    public void FindSampleCue_ReturnsNullForEmptyList()
    {
        Assert.Null(SpeakerSampleExtractorService.FindSampleCue([]));
    }

    // ── ParseSpeakerRows unit tests ────────────────────────────────────────────

    [Fact]
    public void ParseSpeakerRows_ParsesValidRows()
    {
        const string note = "NOTE\nThe conversation contains 2 speakers.\n" +
                             "Speaker1|Female|00:00:00|00:00:26|232Hz\n" +
                             "Speaker2|Male|00:02:58|00:03:26|117Hz";

        var rows = SpeakerSampleExtractorService.ParseSpeakerRows(note);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Speaker1", rows[0].Label);
        Assert.Equal("Female", rows[0].Gender);
        Assert.Equal(TimeSpan.Zero, rows[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(26), rows[0].End);
        Assert.Equal("Speaker2", rows[1].Label);
        Assert.Equal(new TimeSpan(0, 2, 58), rows[1].Start);
        Assert.Equal(new TimeSpan(0, 3, 26), rows[1].End);
    }

    [Fact]
    public void ParseSpeakerRows_ReturnsEmptyForNull()
    {
        Assert.Empty(SpeakerSampleExtractorService.ParseSpeakerRows(null));
    }

    [Fact]
    public void ParseSpeakerRows_SkipsNonDataLines()
    {
        const string note = "NOTE\nThe conversation contains 1 speaker.\nSpeaker1|Male|00:00:01|00:00:02|100Hz";

        var rows = SpeakerSampleExtractorService.ParseSpeakerRows(note);

        Assert.Single(rows);
        Assert.Equal("Speaker1", rows[0].Label);
    }

    // ── ParseCuesBySpeaker unit tests ──────────────────────────────────────────

    [Fact]
    public void ParseCuesBySpeaker_GroupsCuesByLabel()
    {
        var cuesBySpeaker = SpeakerSampleExtractorService.ParseCuesBySpeaker(SampleVttWithSummary);

        Assert.True(cuesBySpeaker.ContainsKey("Speaker1"));
        Assert.True(cuesBySpeaker.ContainsKey("Speaker2"));
        Assert.Single(cuesBySpeaker["Speaker1"]);
        Assert.Equal(2, cuesBySpeaker["Speaker2"].Count);
    }

    [Fact]
    public void ParseCuesBySpeaker_ExtractsCorrectTimingAndText()
    {
        var cuesBySpeaker = SpeakerSampleExtractorService.ParseCuesBySpeaker(SampleVttWithSummary);
        var cue = cuesBySpeaker["Speaker1"][0];

        Assert.Equal(new TimeSpan(0, 0, 0, 0, 31), cue.Start);
        Assert.Equal(new TimeSpan(0, 0, 0, 26, 490), cue.End);
        Assert.Equal("Hello there, this is the longest thing Speaker1 says.", cue.Text);
    }

    [Fact]
    public void ParseCuesBySpeaker_IgnoresSummaryHeaderBlock()
    {
        var cuesBySpeaker = SpeakerSampleExtractorService.ParseCuesBySpeaker(SampleVttWithSummary);

        Assert.DoesNotContain(cuesBySpeaker.Values.SelectMany(v => v), c => c.Text.Contains("speakers"));
    }

    [Fact]
    public void ParseCuesBySpeaker_ReturnsEmptyWhenNoDiarizationNotes()
    {
        var cuesBySpeaker = SpeakerSampleExtractorService.ParseCuesBySpeaker(SampleVttWithoutSummary);

        Assert.Empty(cuesBySpeaker);
    }
}
