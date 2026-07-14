using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using RB.VideoTranslator.Core.Services;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Tests.Core;

public sealed class PipelineOrchestratorTests
{
    // The scenario that triggered this feature: a borderline pitch estimate near
    // vtt_common.py's MALE_FEMALE_F0_THRESHOLD_HZ misclassifying a speaker's gender.
    private const string VttWithSpeakerGender = """
        WEBVTT

        NOTE
        The conversation contains 1 speaker.
        Speaker1|Male|00:00:00|00:00:06|165Hz

        1
        00:00:01.000 --> 00:00:03.000
        Hello world
        """;

    private const string VttWithoutSpeakerData = """
        WEBVTT

        1
        00:00:01.000 --> 00:00:03.000
        Hello world
        """;

    private static VideoJob MakeJob() => new()
    {
        OriginalFileName     = "video.mp4",
        InputFilePath        = "/input/video.mp4",
        ProcessingFolderPath = "/proc",
        VttFilePath          = "/proc/video.vtt",
        State                = JobState.VttExtracted,
    };

    private PipelineOrchestrator MakeSut(
        out IUserPrompt userPrompt,
        out IVoiceRemoverService voiceRemover,
        bool pauseForGenderReview,
        string vttContent,
        VideoJob job)
    {
        var jobService             = Substitute.For<IJobService>();
        var repo                   = Substitute.For<IVideoJobRepository>();
        var mediaSeparator         = Substitute.For<IMediaSeparatorService>();
        var vttExtractor           = Substitute.For<IVttExtractorService>();
        voiceRemover                = Substitute.For<IVoiceRemoverService>();
        var speakerSampleExtractor = Substitute.For<ISpeakerSampleExtractorService>();
        var vttTranslator          = Substitute.For<IVttTranslatorService>();
        var azureTts               = Substitute.For<IVttToAzureTtsService>();
        var audioMixer             = Substitute.For<IAudioMixerService>();
        var videoMuxer             = Substitute.For<IVideoMuxerService>();
        var fs                     = Substitute.For<IFileSystem>();
        userPrompt                  = Substitute.For<IUserPrompt>();

        // GetJobAsync always hands back the same (still-VttExtracted) job — AdvanceAsync's
        // "no progress" guard then stops the loop after exactly one ExecuteStepAsync call,
        // isolating the VttExtracted step for this test rather than simulating the whole
        // downstream pipeline.
        jobService.GetJobAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VideoJob?>(job));
        fs.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(vttContent));

        var options = Options.Create(new PipelineOptions { PauseForGenderReview = pauseForGenderReview });

        return new PipelineOrchestrator(
            jobService, repo, mediaSeparator, vttExtractor, voiceRemover, speakerSampleExtractor,
            vttTranslator, azureTts, audioMixer, videoMuxer, fs, userPrompt, options,
            NullLogger<PipelineOrchestrator>.Instance);
    }

    [Fact]
    public async Task AdvanceAsync_DoesNotPause_WhenPauseForGenderReviewIsDisabled()
    {
        var job = MakeJob();
        var sut = MakeSut(out var userPrompt, out var voiceRemover, pauseForGenderReview: false, VttWithSpeakerGender, job);

        await sut.AdvanceAsync(job);

        await userPrompt.DidNotReceive().WaitForKeyPressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await voiceRemover.Received(1).RemoveAsync(Arg.Any<VideoJob>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_Pauses_WhenEnabledAndSpeakerDataPresent()
    {
        var job = MakeJob();
        var sut = MakeSut(out var userPrompt, out var voiceRemover, pauseForGenderReview: true, VttWithSpeakerGender, job);

        await sut.AdvanceAsync(job);

        await userPrompt.Received(1).WaitForKeyPressAsync(
            Arg.Is<string>(m => m.Contains("Speaker1") && m.Contains("Male")), Arg.Any<CancellationToken>());
        await voiceRemover.Received(1).RemoveAsync(Arg.Any<VideoJob>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_SkipsPause_WhenEnabledButVttHasNoSpeakerData()
    {
        var job = MakeJob();
        var sut = MakeSut(out var userPrompt, out var voiceRemover, pauseForGenderReview: true, VttWithoutSpeakerData, job);

        await sut.AdvanceAsync(job);

        await userPrompt.DidNotReceive().WaitForKeyPressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await voiceRemover.Received(1).RemoveAsync(Arg.Any<VideoJob>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_VoiceRemovalRunsAfterThePause_NotBeforeIt()
    {
        var job = MakeJob();
        var callOrder = new List<string>();
        var sut = MakeSut(out var userPrompt, out var voiceRemover, pauseForGenderReview: true, VttWithSpeakerGender, job);

        userPrompt.WaitForKeyPressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => { callOrder.Add("prompt"); return Task.CompletedTask; });
        voiceRemover.RemoveAsync(Arg.Any<VideoJob>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => { callOrder.Add("voiceRemoval"); return Task.CompletedTask; });

        await sut.AdvanceAsync(job);

        Assert.Equal(["prompt", "voiceRemoval"], callOrder);
    }
}
