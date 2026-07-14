using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using RB.VideoTranslator.Core.Services;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Tests.Core;

public sealed class VttExtractorServiceTests
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly IWhisperOnnxTranscriberService _whisperOnnxTranscriber;
    private readonly VttExtractorService _sut;

    public VttExtractorServiceTests()
    {
        _repo = Substitute.For<IVideoJobRepository>();
        _processRunner = Substitute.For<IProcessRunner>();
        _fs = Substitute.For<IFileSystem>();
        _fs.FileExists(Arg.Any<string>()).Returns(true);
        _whisperOnnxTranscriber = Substitute.For<IWhisperOnnxTranscriberService>();
        _whisperOnnxTranscriber
            .TranscribeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WhisperTranscription(
                [new TranscribedSegment(0, 1.5, "hello there")], "en"));
        _sut = new VttExtractorService(
            _repo,
            _processRunner,
            _fs,
            _whisperOnnxTranscriber,
            NullLogger<VttExtractorService>.Instance);
    }

    private static VideoJob MakeJob(string? extractedAudioPath = "/proc/video_audio.wav") => new()
    {
        OriginalFileName = "video.mp4",
        InputFilePath = "/input/video.mp4",
        ProcessingFolderPath = "/proc",
        ExtractedAudioPath = extractedAudioPath
    };

    [Fact]
    public async Task ExtractAsync_ThrowsWhenExtractedAudioPathIsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob(extractedAudioPath: null)));
    }

    [Fact]
    public async Task ExtractAsync_ThrowsWhenExtractedAudioPathIsEmpty()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob(extractedAudioPath: "")));
    }

    [Fact]
    public async Task ExtractAsync_SetsVttFilePath()
    {
        var job = MakeJob();

        await _sut.ExtractAsync(job);

        Assert.Equal(Path.Combine("/proc", "video.vtt"), job.VttFilePath);
    }

    [Fact]
    public async Task ExtractAsync_TransitionsStateToVttExtracted()
    {
        var job = MakeJob();

        await _sut.ExtractAsync(job);

        await _repo.Received(1).UpdateAsync(
            Arg.Is<VideoJob>(j => j.State == JobState.VttExtracted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_InvokesPythonOnce()
    {
        await _sut.ExtractAsync(MakeJob(), "python");

        await _processRunner.Received(1)
            .RunAsync("python", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_ThrowsWhenVttOutputFileMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.ExtractAsync(MakeJob()));
    }

    [Fact]
    public async Task ExtractAsync_DoesNotUpdateDbWhenVttOutputFileMissing()
    {
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        try { await _sut.ExtractAsync(MakeJob()); } catch (FileNotFoundException) { }

        await _repo.DidNotReceive().UpdateAsync(Arg.Any<VideoJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_PropagatesProcessRunnerException()
    {
        _processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("whisper exited with code 1."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob()));
    }

    [Fact]
    public async Task ExtractAsync_OmitsNoVoiceMarksFlagByDefault()
    {
        await _sut.ExtractAsync(MakeJob());

        await _processRunner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => !a.Contains("--no-voice-marks")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_PassesNoVoiceMarksFlagWhenDisabled()
    {
        await _sut.ExtractAsync(MakeJob(), "python", enableVoiceMarks: false);

        await _processRunner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("--no-voice-marks")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_OnnxThrowsWhenModelPathMissing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExtractAsync(MakeJob(), useOnnxTranscription: true, onnxModelPath: null));
    }

    [Fact]
    public async Task ExtractAsync_OnnxWithVoiceMarksWritesSegmentsJsonWithLowercaseKeys()
    {
        // vtt_common.diarize_and_shape()/whisperx expect lowercase dict keys ("start"/"end"/
        // "text"); plain C# JsonSerializer.Serialize() defaults to PascalCase, which whisperx
        // silently mishandles rather than rejecting — regression test for that bug.
        var job = MakeJob();

        await _sut.ExtractAsync(job, "python", enableVoiceMarks: true, useOnnxTranscription: true,
            onnxModelPath: "/models/whisper-medium", ffmpegPath: "ffmpeg");

        await _fs.Received(1).WriteAllTextAsync(
            Arg.Is<string>(p => p.EndsWith("_segments.json")),
            Arg.Is<string>(content =>
                content.Contains("\"start\"") && content.Contains("\"end\"") && content.Contains("\"text\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_OnnxWithVoiceMarksCallsTranscriberThenDiarizeScript()
    {
        var job = MakeJob();

        await _sut.ExtractAsync(job, "python", enableVoiceMarks: true, useOnnxTranscription: true,
            onnxModelPath: "/models/whisper-medium", ffmpegPath: "ffmpeg");

        await _whisperOnnxTranscriber.Received(1).TranscribeAsync(
            job.ExtractedAudioPath!, "/models/whisper-medium", "ffmpeg", Arg.Any<CancellationToken>());
        await _processRunner.Received(1).RunAsync(
            "python",
            Arg.Is<string>(a => a.Contains("tool_diarizeVtt.py") && a.Contains("--language en")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_OnnxWithoutVoiceMarksWritesVttDirectlyWithoutPythonDiarizeCall()
    {
        var job = MakeJob();

        await _sut.ExtractAsync(job, "python", enableVoiceMarks: false, useOnnxTranscription: true,
            onnxModelPath: "/models/whisper-medium", ffmpegPath: "ffmpeg");

        await _whisperOnnxTranscriber.Received(1).TranscribeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fs.Received(1).WriteAllTextAsync(
            job.VttFilePath!,
            Arg.Is<string>(content => content.StartsWith("WEBVTT") && content.Contains("hello there")),
            Arg.Any<CancellationToken>());
        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_OnnxSetsVttFilePathAndState()
    {
        var job = MakeJob();

        await _sut.ExtractAsync(job, "python", enableVoiceMarks: false, useOnnxTranscription: true,
            onnxModelPath: "/models/whisper-medium");

        Assert.Equal(Path.Combine("/proc", "video.vtt"), job.VttFilePath);
        Assert.Equal(JobState.VttExtracted, job.State);
    }
}
