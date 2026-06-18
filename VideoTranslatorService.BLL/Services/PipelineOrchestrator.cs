using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public sealed class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly IJobService _jobService;
    private readonly IMediaSeparatorService _mediaSeparator;
    private readonly ISrtExtractorService _srtExtractor;
    private readonly IVoiceRemoverService _voiceRemover;
    private readonly ISrtTranslatorService _srtTranslator;
    private readonly IVoiceSynthesiserService _voiceSynthesiser;
    private readonly IAudioMixerService _audioMixer;
    private readonly IVideoMuxerService _videoMuxer;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        IJobService jobService,
        IMediaSeparatorService mediaSeparator,
        ISrtExtractorService srtExtractor,
        IVoiceRemoverService voiceRemover,
        ISrtTranslatorService srtTranslator,
        IVoiceSynthesiserService voiceSynthesiser,
        IAudioMixerService audioMixer,
        IVideoMuxerService videoMuxer,
        ILogger<PipelineOrchestrator> logger)
    {
        _jobService = jobService;
        _mediaSeparator = mediaSeparator;
        _srtExtractor = srtExtractor;
        _voiceRemover = voiceRemover;
        _srtTranslator = srtTranslator;
        _voiceSynthesiser = voiceSynthesiser;
        _audioMixer = audioMixer;
        _videoMuxer = videoMuxer;
        _logger = logger;
    }

    private static bool IsTerminal(JobState state) =>
        state is JobState.AddedToOriginalVideo or JobState.Completed or JobState.Failed;

    // Maps each in-progress state back to the stable state that precedes it.
    // Used to reset jobs that were interrupted mid-step (e.g. process crash).
    private static readonly Dictionary<JobState, JobState> StablePredecessor = new()
    {
        [JobState.SeparatingMedia]  = JobState.Queued,
        [JobState.ExtractingSrt]    = JobState.AudioExtracted,
        [JobState.RemovingVoice]    = JobState.SrtExtracted,
        [JobState.TranslatingSrt]   = JobState.VoiceRemoved,
        [JobState.SynthesisingVoice]= JobState.SrtTranslated,
        [JobState.MixingAudio]      = JobState.VoiceSynthesised,
        [JobState.AddingToVideo]    = JobState.MixedNoVoiceWithSyntheticVoice,
    };

    public async Task AdvanceAsync(VideoJob job, PipelineOptions options, CancellationToken ct = default)
    {
        // If the service was restarted while a step was running, the job will be
        // stuck in an in-progress state. Reset it to the stable predecessor so the
        // step is retried cleanly from the beginning.
        if (StablePredecessor.TryGetValue(job.State, out var stableState))
        {
            _logger.LogWarning(
                "Job {Id} ({File}) was interrupted at {Interrupted} — resetting to {Stable} and retrying",
                job.Id, job.OriginalFileName, job.State, stableState);

            await _jobService.TransitionStateAsync(job.Id, stableState, ct: ct);
            job = (await _jobService.GetJobAsync(job.Id, ct))!;
        }

        while (!IsTerminal(job.State))
        {
            var stateBefore = job.State;
            try
            {
                await ExecuteStepAsync(job, options, ct);
                job = (await _jobService.GetJobAsync(job.Id, ct))!;

                if (job.State == stateBefore)
                {
                    _logger.LogWarning("Job {Id} made no progress from {State} — stopping.", job.Id, stateBefore);
                    break;
                }

                _logger.LogInformation("Job {Id} advanced: {Before} → {After}", job.Id, stateBefore, job.State);
            }
            catch (StepNotImplementedException ex)
            {
                _logger.LogInformation(
                    "Job {Id} paused at {State} — will advance once the step is available. ({Message})",
                    job.Id, stateBefore, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {Id} failed during {State}", job.Id, stateBefore);
                await _jobService.TransitionStateAsync(job.Id, JobState.Failed, ex.Message, ct);
                break;
            }
        }

        if (IsTerminal(job.State))
            _logger.LogInformation("Job {Id} reached terminal state: {State}", job.Id, job.State);
    }

    private async Task ExecuteStepAsync(VideoJob job, PipelineOptions options, CancellationToken ct)
    {
        switch (job.State)
        {
            case JobState.Queued:
                await _jobService.TransitionStateAsync(job.Id, JobState.SeparatingMedia, ct: ct);
                var separating = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _mediaSeparator.SeparateAsync(separating, options.FfmpegPath, ct);
                break;

            case JobState.AudioExtracted:
                await _jobService.TransitionStateAsync(job.Id, JobState.ExtractingSrt, ct: ct);
                var extracting = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _srtExtractor.ExtractAsync(extracting, options.PythonPath, ct);
                break;

            case JobState.SrtExtracted:
                await _voiceRemover.RemoveAsync(job, ct);
                break;

            case JobState.VoiceRemoved:
                await _srtTranslator.TranslateAsync(job, ct);
                break;

            case JobState.SrtTranslated:
                await _voiceSynthesiser.SynthesiseAsync(job, ct);
                break;

            case JobState.VoiceSynthesised:
                await _audioMixer.MixAsync(job, ct);
                break;

            case JobState.MixedNoVoiceWithSyntheticVoice:
                await _videoMuxer.MuxAsync(job, options.FfmpegPath, ct);
                break;
        }
    }
}
