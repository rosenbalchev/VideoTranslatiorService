using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public sealed class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly IJobService _jobService;
    private readonly IMediaSeparatorService _mediaSeparator;
    private readonly ISrtExtractorService _srtExtractor;
    private readonly ISrtTranslatorService _srtTranslator;
    private readonly ISrtToAzureTtsService _azureTts;
    private readonly IVoiceRemoverService _voiceRemover;
    private readonly IAudioMixerService _audioMixer;
    private readonly IVideoMuxerService _videoMuxer;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        IJobService jobService,
        IMediaSeparatorService mediaSeparator,
        ISrtExtractorService srtExtractor,
        ISrtTranslatorService srtTranslator,
        ISrtToAzureTtsService azureTts,
        IVoiceRemoverService voiceRemover,
        IAudioMixerService audioMixer,
        IVideoMuxerService videoMuxer,
        ILogger<PipelineOrchestrator> logger)
    {
        _jobService     = jobService;
        _mediaSeparator = mediaSeparator;
        _srtExtractor   = srtExtractor;
        _srtTranslator  = srtTranslator;
        _azureTts       = azureTts;
        _voiceRemover   = voiceRemover;
        _audioMixer     = audioMixer;
        _videoMuxer     = videoMuxer;
        _logger         = logger;
    }

    private static bool IsTerminal(JobState state) =>
        state is JobState.AddedToOriginalVideo or JobState.Completed or JobState.Failed;

    // Maps each in-progress state back to the stable state that precedes it.
    // Used to reset jobs that were interrupted mid-step (e.g. process crash).
    private static readonly Dictionary<JobState, JobState> StablePredecessor = new()
    {
        [JobState.SeparatingMedia]             = JobState.Queued,
        [JobState.ExtractingSrt]               = JobState.AudioExtracted,
        [JobState.TranslatingSrt]              = JobState.SrtExtracted,
        [JobState.SynthesisingAzureTts]        = JobState.SrtTranslated,
        [JobState.RemovingVoice]               = JobState.AzureTtsSynthesised,
        [JobState.MixingAudio]                 = JobState.VoiceRemoved,
        [JobState.AddingToVideo]               = JobState.MixedNoVoiceWithSyntheticVoice,
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
                _logger.LogError(ex,
                    "Job {Id} failed during {State} — resetting to {Stable} so it is retried on the next run",
                    job.Id, stateBefore, stateBefore);
                await _jobService.TransitionStateAsync(job.Id, stateBefore, ex.Message, ct);
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
                await _jobService.TransitionStateAsync(job.Id, JobState.TranslatingSrt, ct: ct);
                var translating = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _srtTranslator.TranslateAsync(translating, options.TranslationTargetLanguage, ct);
                break;

            case JobState.SrtTranslated:
                await _jobService.TransitionStateAsync(job.Id, JobState.SynthesisingAzureTts, ct: ct);
                var ttsJob = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _azureTts.SynthesiseAsync(
                    ttsJob,
                    options.AzureSubscriptionKey,
                    options.AzureEndpointUrl,
                    options.AzureTtsVoiceName,
                    options.AzureTtsLang,
                    ct);
                break;

            case JobState.AzureTtsSynthesised:
                await _jobService.TransitionStateAsync(job.Id, JobState.RemovingVoice, ct: ct);
                var removing = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _voiceRemover.RemoveAsync(removing, options.DemucsPath, ct);
                break;

            case JobState.VoiceRemoved:
                await _jobService.TransitionStateAsync(job.Id, JobState.MixingAudio, ct: ct);
                var mixing = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _audioMixer.MixAsync(mixing, options.FfmpegPath, ct);
                break;

            case JobState.MixedNoVoiceWithSyntheticVoice:
                await _jobService.TransitionStateAsync(job.Id, JobState.AddingToVideo, ct: ct);
                var muxing = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _videoMuxer.MuxAsync(muxing, options.FfmpegPath, options.OutputFolderPath, ct);
                break;
        }
    }
}
