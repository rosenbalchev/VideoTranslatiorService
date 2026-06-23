using System.Text.Json;
using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly IJobService _jobService;
    private readonly IVideoJobRepository _repo;
    private readonly IMediaSeparatorService _mediaSeparator;
    private readonly ISrtExtractorService _srtExtractor;
    private readonly IVoiceRemoverService _voiceRemover;
    private readonly ISrtTranslatorService _srtTranslator;
    private readonly ISrtToAzureTtsService _azureTts;
    private readonly IAudioMixerService _audioMixer;
    private readonly IVideoMuxerService _videoMuxer;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        IJobService jobService,
        IVideoJobRepository repo,
        IMediaSeparatorService mediaSeparator,
        ISrtExtractorService srtExtractor,
        IVoiceRemoverService voiceRemover,
        ISrtTranslatorService srtTranslator,
        ISrtToAzureTtsService azureTts,
        IAudioMixerService audioMixer,
        IVideoMuxerService videoMuxer,
        ILogger<PipelineOrchestrator> logger)
    {
        _jobService     = jobService;
        _repo           = repo;
        _mediaSeparator = mediaSeparator;
        _srtExtractor   = srtExtractor;
        _voiceRemover   = voiceRemover;
        _srtTranslator  = srtTranslator;
        _azureTts       = azureTts;
        _audioMixer     = audioMixer;
        _videoMuxer     = videoMuxer;
        _logger         = logger;
    }

    // Maps display language names (as passed to --target-lang) to Azure TTS voice + BCP-47 tag.
    // Add entries here to support additional languages.
    private static readonly Dictionary<string, (string VoiceName, string Lang)> VoiceMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Bulgarian"]  = ("bg-BG-BorislavNeural",  "bg-BG"),
            ["Chinese"]    = ("zh-CN-XiaoxiaoNeural",  "zh-CN"),
            ["Croatian"]   = ("hr-HR-SreckoNeural",    "hr-HR"),
            ["Czech"]      = ("cs-CZ-VlastaNeural",    "cs-CZ"),
            ["Danish"]     = ("da-DK-ChristelNeural",  "da-DK"),
            ["Dutch"]      = ("nl-NL-ColetteNeural",   "nl-NL"),
            ["English"]    = ("en-US-AvaNeural",       "en-US"),
            ["Finnish"]    = ("fi-FI-NooraNeural",     "fi-FI"),
            ["French"]     = ("fr-FR-DeniseNeural",    "fr-FR"),
            ["German"]     = ("de-DE-KatjaNeural",     "de-DE"),
            ["Greek"]      = ("el-GR-AthinaNeural",    "el-GR"),
            ["Hungarian"]  = ("hu-HU-NoemiNeural",     "hu-HU"),
            ["Italian"]    = ("it-IT-ElsaNeural",      "it-IT"),
            ["Japanese"]   = ("ja-JP-NanamiNeural",    "ja-JP"),
            ["Korean"]     = ("ko-KR-SunHiNeural",     "ko-KR"),
            ["Norwegian"]  = ("nb-NO-PernilleNeural",  "nb-NO"),
            ["Polish"]     = ("pl-PL-ZofiaNeural",     "pl-PL"),
            ["Portuguese"] = ("pt-BR-FranciscaNeural", "pt-BR"),
            ["Romanian"]   = ("ro-RO-AlinaNeural",     "ro-RO"),
            ["Russian"]    = ("ru-RU-SvetlanaNeural",  "ru-RU"),
            ["Slovak"]     = ("sk-SK-ViktoriaNeural",  "sk-SK"),
            ["Slovenian"]  = ("sl-SI-PetraNeural",     "sl-SI"),
            ["Spanish"]    = ("es-ES-ElviraNeural",    "es-ES"),
            ["Swedish"]    = ("sv-SE-SofieNeural",     "sv-SE"),
            ["Turkish"]    = ("tr-TR-EmelNeural",      "tr-TR"),
            ["Ukrainian"]  = ("uk-UA-PolinaNeural",    "uk-UA"),
        };

    private static bool IsTerminal(JobState state) =>
        state is JobState.AddedToOriginalVideo or JobState.Completed or JobState.Failed;

    // Maps each in-progress state back to the stable state that precedes it.
    // States inside the multi-language loop (TranslatingSrt..MixingAudio) all reset to
    // VoiceRemoved so the entire language loop is retried cleanly.
    private static readonly Dictionary<JobState, JobState> StablePredecessor = new()
    {
        [JobState.SeparatingMedia]      = JobState.Queued,
        [JobState.ExtractingSrt]        = JobState.AudioExtracted,
        [JobState.RemovingVoice]        = JobState.SrtExtracted,
        // Multi-language loop — any crash inside resets to VoiceRemoved
        [JobState.TranslatingSrt]       = JobState.VoiceRemoved,
        [JobState.SrtTranslated]        = JobState.VoiceRemoved,
        [JobState.SynthesisingAzureTts] = JobState.VoiceRemoved,
        [JobState.AzureTtsSynthesised]  = JobState.VoiceRemoved,
        [JobState.MixingAudio]          = JobState.VoiceRemoved,
        [JobState.AddingToVideo]        = JobState.MixedNoVoiceWithSyntheticVoice,
    };

    public async Task AdvanceAsync(VideoJob job, PipelineOptions options, CancellationToken ct = default)
    {
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
                    "Job {Id} failed during {State}",
                    job.Id, stateBefore);
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
                // Voice removal runs here — it only needs the extracted audio and
                // is independent of subtitles, so it runs once before any language work.
                await _jobService.TransitionStateAsync(job.Id, JobState.RemovingVoice, ct: ct);
                var removing = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _voiceRemover.RemoveAsync(removing, options.DemucsPath, ct);
                break;

            case JobState.VoiceRemoved:
                // Mark start of the multi-language loop.
                await _jobService.TransitionStateAsync(job.Id, JobState.TranslatingSrt, ct: ct);
                var working = (await _jobService.GetJobAsync(job.Id, ct))!;

                // Resume from any previously completed languages so that stopping and
                // restarting mid-loop does not redo already-finished work.
                var results = string.IsNullOrEmpty(working.LanguageResultsJson)
                    ? new List<LanguageResult>(options.TranslationTargetLanguages.Length)
                    : JsonSerializer.Deserialize<List<LanguageResult>>(working.LanguageResultsJson)!;

                var done = results.Select(r => r.Language)
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var lang in options.TranslationTargetLanguages)
                {
                    if (done.Contains(lang))
                    {
                        _logger.LogInformation("Language {Lang} already completed — skipping", lang);
                        continue;
                    }

                    if (!VoiceMap.TryGetValue(lang, out var voice))
                        throw new InvalidOperationException(
                            $"No Azure TTS voice configured for language '{lang}'. " +
                            $"Add an entry to PipelineOrchestrator.VoiceMap.");

                    _logger.LogInformation("Processing language: {Language} (voice: {Voice})", lang, voice.VoiceName);

                    await _srtTranslator.TranslateAsync(working, lang, ct);
                    var translatedPath = working.TranslatedSrtFilePath!;

                    await _azureTts.SynthesiseAsync(
                        working,
                        options.AzureSubscriptionKey,
                        options.AzureEndpointUrl,
                        voice.VoiceName,
                        voice.Lang,
                        ct);

                    await _audioMixer.MixAsync(working, options.FfmpegPath, ct);

                    results.Add(new LanguageResult(lang, working.MixedAudioPath!, translatedPath));

                    // Persist progress after every language. AudioMixerService already wrote
                    // MixedNoVoiceWithSyntheticVoice; override state back to TranslatingSrt
                    // until the final language is done so crash recovery (StablePredecessor
                    // TranslatingSrt → VoiceRemoved) restores the job correctly and the loop
                    // skips already-completed languages on the next run.
                    var allDone = options.TranslationTargetLanguages
                        .All(l => results.Any(r => string.Equals(r.Language, l, StringComparison.OrdinalIgnoreCase)));

                    working.LanguageResultsJson = JsonSerializer.Serialize(results);
                    working.State               = allDone
                        ? JobState.MixedNoVoiceWithSyntheticVoice
                        : JobState.TranslatingSrt;
                    await _repo.UpdateAsync(working, ct);
                }
                break;

            case JobState.MixedNoVoiceWithSyntheticVoice:
                await _jobService.TransitionStateAsync(job.Id, JobState.AddingToVideo, ct: ct);
                var muxing = (await _jobService.GetJobAsync(job.Id, ct))!;

                var languageResults = JsonSerializer.Deserialize<List<LanguageResult>>(
                    muxing.LanguageResultsJson ?? "[]")!;

                await _videoMuxer.MuxAsync(
                    muxing, options.FfmpegPath, options.OutputFolderPath, languageResults, ct);
                break;
        }
    }
}
