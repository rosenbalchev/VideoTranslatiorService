using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Exceptions;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Core.Services;

public sealed class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly IJobService _jobService;
    private readonly IVideoJobRepository _repo;
    private readonly IMediaSeparatorService _mediaSeparator;
    private readonly IVttExtractorService _vttExtractor;
    private readonly IVoiceRemoverService _voiceRemover;
    private readonly ISpeakerSampleExtractorService _speakerSampleExtractor;
    private readonly IVttTranslatorService _vttTranslator;
    private readonly IVttToAzureTtsService _azureTts;
    private readonly IAudioMixerService _audioMixer;
    private readonly IVideoMuxerService _videoMuxer;
    private readonly IFileSystem _fs;
    private readonly IUserPrompt _userPrompt;
    private readonly IOptions<PipelineOptions> _options;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        IJobService jobService,
        IVideoJobRepository repo,
        IMediaSeparatorService mediaSeparator,
        IVttExtractorService vttExtractor,
        IVoiceRemoverService voiceRemover,
        ISpeakerSampleExtractorService speakerSampleExtractor,
        IVttTranslatorService vttTranslator,
        IVttToAzureTtsService azureTts,
        IAudioMixerService audioMixer,
        IVideoMuxerService videoMuxer,
        IFileSystem fs,
        IUserPrompt userPrompt,
        IOptions<PipelineOptions> options,
        ILogger<PipelineOrchestrator> logger)
    {
        _jobService     = jobService;
        _repo           = repo;
        _mediaSeparator = mediaSeparator;
        _vttExtractor   = vttExtractor;
        _voiceRemover   = voiceRemover;
        _speakerSampleExtractor = speakerSampleExtractor;
        _vttTranslator  = vttTranslator;
        _azureTts       = azureTts;
        _audioMixer     = audioMixer;
        _videoMuxer     = videoMuxer;
        _fs             = fs;
        _userPrompt     = userPrompt;
        _options        = options;
        _logger         = logger;
    }

    // Maps display language names (as passed to --target-lang) to every broadly-available
    // standard Azure Neural TTS voice for that locale, grouped by gender, plus the BCP-47 tag.
    // Add entries here to support additional languages. The FIRST voice in each gender's list
    // is what gets used today (PipelineOrchestrator picks index 0) — extra voices exist so a
    // future per-speaker assignment (see SpeakerVoiceAssigner) has distinct options to hand out
    // when a language has multiple same-gender speakers.
    // Deliberately excludes "Multilingual", ":MAI-Voice-*", "Turbo", and dialect/regional
    // variants (e.g. wuu-CN, yue-CN, zh-CN-liaoning-*) — those require preview/limited access
    // or are a different locale entirely; the voices below work on any standard Speech resource.
    // Voice names sourced from: https://learn.microsoft.com/azure/ai-services/speech-service/language-support?tabs=tts
    private static readonly Dictionary<string, (string[] MaleVoices, string[] FemaleVoices, string Lang)> VoiceMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Bulgarian"] = (
                MaleVoices:   ["bg-BG-BorislavNeural"],
                FemaleVoices: ["bg-BG-KalinaNeural"],
                Lang: "bg-BG"),
            ["Chinese"] = (
                MaleVoices:   ["zh-CN-YunxiNeural", "zh-CN-XiaoyouNeural", "zh-CN-YunyeNeural", "zh-CN-YunyangNeural"],
                FemaleVoices: ["zh-CN-XiaoxiaoNeural", "zh-CN-XiaozhenNeural", "zh-CN-YunxiaNeural"],
                Lang: "zh-CN"),
            ["Croatian"] = (
                MaleVoices:   ["hr-HR-SreckoNeural"],
                FemaleVoices: ["hr-HR-GabrijelaNeural"],
                Lang: "hr-HR"),
            ["Czech"] = (
                MaleVoices:   ["cs-CZ-AntoninNeural"],
                FemaleVoices: ["cs-CZ-VlastaNeural"],
                Lang: "cs-CZ"),
            ["Danish"] = (
                MaleVoices:   ["da-DK-JeppeNeural"],
                FemaleVoices: ["da-DK-ChristelNeural"],
                Lang: "da-DK"),
            ["Dutch"] = (
                MaleVoices:   ["nl-NL-MaartenNeural"],
                FemaleVoices: ["nl-NL-ColetteNeural", "nl-NL-FennaNeural"],
                Lang: "nl-NL"),
            ["English"] = (
                MaleVoices:   ["en-US-GuyNeural", "en-US-AndrewNeural", "en-US-BrianNeural", "en-US-DavisNeural",
                               "en-US-JasonNeural", "en-US-KaiNeural", "en-US-TonyNeural", "en-US-BrandonNeural",
                               "en-US-ChristopherNeural", "en-US-EricNeural", "en-US-JacobNeural", "en-US-RogerNeural",
                               "en-US-SteffanNeural"],
                FemaleVoices: ["en-US-AvaNeural", "en-US-EmmaNeural", "en-US-JennyNeural", "en-US-AriaNeural",
                               "en-US-JaneNeural", "en-US-LunaNeural", "en-US-SaraNeural", "en-US-NancyNeural",
                               "en-US-AmberNeural", "en-US-AnaNeural", "en-US-AshleyNeural", "en-US-CoraNeural",
                               "en-US-ElizabethNeural", "en-US-MichelleNeural", "en-US-MonicaNeural"],
                Lang: "en-US"),
            ["Finnish"] = (
                MaleVoices:   ["fi-FI-HarriNeural"],
                FemaleVoices: ["fi-FI-NooraNeural", "fi-FI-SelmaNeural"],
                Lang: "fi-FI"),
            ["French"] = (
                MaleVoices:   ["fr-FR-HenriNeural", "fr-FR-AlainNeural", "fr-FR-ClaudeNeural", "fr-FR-JeromeNeural",
                               "fr-FR-MauriceNeural", "fr-FR-YvesNeural"],
                FemaleVoices: ["fr-FR-DeniseNeural", "fr-FR-BrigitteNeural", "fr-FR-CelesteNeural", "fr-FR-CoralieNeural",
                               "fr-FR-EloiseNeural", "fr-FR-JacquelineNeural", "fr-FR-JosephineNeural", "fr-FR-YvetteNeural"],
                Lang: "fr-FR"),
            ["German"] = (
                MaleVoices:   ["de-DE-ConradNeural", "de-DE-BerndNeural", "de-DE-ChristophNeural", "de-DE-KasperNeural",
                               "de-DE-KillianNeural", "de-DE-KlausNeural", "de-DE-RalfNeural"],
                FemaleVoices: ["de-DE-KatjaNeural", "de-DE-AmalaNeural", "de-DE-ElkeNeural", "de-DE-GiselaNeural",
                               "de-DE-KlarissaNeural", "de-DE-LouisaNeural", "de-DE-MajaNeural", "de-DE-TanjaNeural"],
                Lang: "de-DE"),
            ["Greek"] = (
                MaleVoices:   ["el-GR-NestorasNeural"],
                FemaleVoices: ["el-GR-AthinaNeural"],
                Lang: "el-GR"),
            ["Hungarian"] = (
                MaleVoices:   ["hu-HU-TamasNeural"],
                FemaleVoices: ["hu-HU-NoemiNeural"],
                Lang: "hu-HU"),
            ["Italian"] = (
                MaleVoices:   ["it-IT-DiegoNeural", "it-IT-BenignoNeural", "it-IT-CalimeroNeural", "it-IT-CataldoNeural",
                               "it-IT-GianniNeural", "it-IT-GiuseppeNeural", "it-IT-LisandroNeural", "it-IT-RinaldoNeural"],
                FemaleVoices: ["it-IT-ElsaNeural", "it-IT-IsabellaNeural", "it-IT-FabiolaNeural", "it-IT-FiammaNeural",
                               "it-IT-ImeldaNeural", "it-IT-IrmaNeural", "it-IT-PalmiraNeural", "it-IT-PierinaNeural"],
                Lang: "it-IT"),
            ["Japanese"] = (
                MaleVoices:   ["ja-JP-KeitaNeural", "ja-JP-DaichiNeural", "ja-JP-NaokiNeural"],
                FemaleVoices: ["ja-JP-NanamiNeural", "ja-JP-AoiNeural", "ja-JP-MayuNeural", "ja-JP-ShioriNeural"],
                Lang: "ja-JP"),
            ["Korean"] = (
                MaleVoices:   ["ko-KR-InJoonNeural", "ko-KR-BongJinNeural", "ko-KR-GookMinNeural", "ko-KR-HyunsuNeural"],
                FemaleVoices: ["ko-KR-SunHiNeural", "ko-KR-JiMinNeural", "ko-KR-SeoHyeonNeural", "ko-KR-SoonBokNeural",
                               "ko-KR-YuJinNeural"],
                Lang: "ko-KR"),
            ["Norwegian"] = (
                MaleVoices:   ["nb-NO-FinnNeural"],
                FemaleVoices: ["nb-NO-PernilleNeural", "nb-NO-IselinNeural"],
                Lang: "nb-NO"),
            ["Polish"] = (
                MaleVoices:   ["pl-PL-MarekNeural"],
                FemaleVoices: ["pl-PL-ZofiaNeural", "pl-PL-AgnieszkaNeural"],
                Lang: "pl-PL"),
            ["Portuguese"] = (
                MaleVoices:   ["pt-BR-AntonioNeural", "pt-BR-DonatoNeural", "pt-BR-FabioNeural", "pt-BR-HumbertoNeural",
                               "pt-BR-JulioNeural", "pt-BR-NicolauNeural", "pt-BR-ValerioNeural"],
                FemaleVoices: ["pt-BR-FranciscaNeural", "pt-BR-BrendaNeural", "pt-BR-ElzaNeural", "pt-BR-GiovannaNeural",
                               "pt-BR-LeilaNeural", "pt-BR-LeticiaNeural", "pt-BR-ManuelaNeural", "pt-BR-ThalitaNeural",
                               "pt-BR-YaraNeural"],
                Lang: "pt-BR"),
            ["Romanian"] = (
                MaleVoices:   ["ro-RO-EmilNeural"],
                FemaleVoices: ["ro-RO-AlinaNeural"],
                Lang: "ro-RO"),
            ["Russian"] = (
                MaleVoices:   ["ru-RU-DmitryNeural"],
                FemaleVoices: ["ru-RU-SvetlanaNeural", "ru-RU-DariyaNeural"],
                Lang: "ru-RU"),
            ["Slovak"] = (
                MaleVoices:   ["sk-SK-LukasNeural"],
                FemaleVoices: ["sk-SK-ViktoriaNeural"],
                Lang: "sk-SK"),
            ["Slovenian"] = (
                MaleVoices:   ["sl-SI-RokNeural"],
                FemaleVoices: ["sl-SI-PetraNeural"],
                Lang: "sl-SI"),
            ["Spanish"] = (
                MaleVoices:   ["es-ES-AlvaroNeural", "es-ES-ArnauNeural", "es-ES-DarioNeural", "es-ES-EliasNeural",
                               "es-ES-NilNeural", "es-ES-SaulNeural", "es-ES-TeoNeural"],
                FemaleVoices: ["es-ES-ElviraNeural", "es-ES-AbrilNeural", "es-ES-EstrellaNeural", "es-ES-IreneNeural",
                               "es-ES-LaiaNeural", "es-ES-LiaNeural", "es-ES-TrianaNeural", "es-ES-VeraNeural",
                               "es-ES-XimenaNeural"],
                Lang: "es-ES"),
            ["Swedish"] = (
                MaleVoices:   ["sv-SE-MattiasNeural"],
                FemaleVoices: ["sv-SE-SofieNeural", "sv-SE-HilleviNeural"],
                Lang: "sv-SE"),
            ["Turkish"] = (
                MaleVoices:   ["tr-TR-AhmetNeural"],
                FemaleVoices: ["tr-TR-EmelNeural"],
                Lang: "tr-TR"),
            ["Ukrainian"] = (
                MaleVoices:   ["uk-UA-OstapNeural"],
                FemaleVoices: ["uk-UA-PolinaNeural"],
                Lang: "uk-UA"),
        };

    private static bool IsTerminal(JobState state) =>
        state is JobState.AddedToOriginalVideo or JobState.Completed or JobState.Failed;

    // Maps each in-progress state back to the stable state that precedes it.
    // States inside the multi-language loop (TranslatingVtt..MixingAudio) all reset to
    // VoiceRemoved so the entire language loop is retried cleanly.
    private static readonly Dictionary<JobState, JobState> StablePredecessor = new()
    {
        [JobState.SeparatingMedia]      = JobState.Queued,
        [JobState.ExtractingVtt]        = JobState.AudioExtracted,
        [JobState.RemovingVoice]        = JobState.VttExtracted,
        [JobState.ExtractingSpeakerSamples] = JobState.VoiceRemoved,
        // Multi-language loop — any crash inside resets to SpeakerSamplesExtracted
        [JobState.TranslatingVtt]       = JobState.SpeakerSamplesExtracted,
        [JobState.VttTranslated]        = JobState.SpeakerSamplesExtracted,
        [JobState.SynthesisingAzureTts] = JobState.SpeakerSamplesExtracted,
        [JobState.AzureTtsSynthesised]  = JobState.SpeakerSamplesExtracted,
        [JobState.MixingAudio]          = JobState.SpeakerSamplesExtracted,
        [JobState.AddingToVideo]        = JobState.MixedNoVoiceWithSyntheticVoice,
    };

    public async Task AdvanceAsync(VideoJob job, CancellationToken ct = default)
    {
        var options = _options.Value;
        var sw = Stopwatch.StartNew();

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

        sw.Stop();
        if (job.State == JobState.AddedToOriginalVideo)
            _logger.LogInformation(
                "Operation completed: {File} — total time {Elapsed:F0} seconds. Output: {Output}",
                job.OriginalFileName, sw.Elapsed.TotalSeconds, job.OutputFilePath);
        else if (IsTerminal(job.State))
            _logger.LogInformation(
                "Job {Id} reached terminal state {State} after {Elapsed:F0} seconds",
                job.Id, job.State, sw.Elapsed.TotalSeconds);
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
                await _jobService.TransitionStateAsync(job.Id, JobState.ExtractingVtt, ct: ct);
                var extracting = (await _jobService.GetJobAsync(job.Id, ct))!;
                // Fixed convention matching scripts/export_onnx_models.{bat,sh}'s output location —
                // not a separate appsettings key, so it always follows WorkingFolderPath.
                var onnxModelPath = options.UseOnnxTranscription
                    ? Path.Combine(options.WorkingFolderPath, "onnx-models", "whisper-medium")
                    : null;
                await _vttExtractor.ExtractAsync(
                    extracting, options.PythonPath, options.EnableVoiceMarks,
                    options.UseOnnxTranscription, onnxModelPath, options.FfmpegPath, ct);
                break;

            case JobState.VttExtracted:
                // Still in VttExtracted (not yet transitioned) while paused, so a restart
                // mid-pause simply re-enters this case and pauses again — nothing is lost.
                if (options.PauseForGenderReview)
                    await PauseForGenderReviewAsync(job, ct);

                // Voice removal runs here — it only needs the extracted audio and
                // is independent of subtitles, so it runs once before any language work.
                await _jobService.TransitionStateAsync(job.Id, JobState.RemovingVoice, ct: ct);
                var removing = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _voiceRemover.RemoveAsync(removing, options.DemucsPath, ct);
                break;

            case JobState.VoiceRemoved:
                await _jobService.TransitionStateAsync(job.Id, JobState.ExtractingSpeakerSamples, ct: ct);
                var extractingSamples = (await _jobService.GetJobAsync(job.Id, ct))!;
                await _speakerSampleExtractor.ExtractAsync(extractingSamples, options.FfmpegPath, ct);
                break;

            case JobState.SpeakerSamplesExtracted:
                // Mark start of the multi-language loop.
                await _jobService.TransitionStateAsync(job.Id, JobState.TranslatingVtt, ct: ct);
                var working = (await _jobService.GetJobAsync(job.Id, ct))!;

                // Resume from any previously completed languages so that stopping and
                // restarting mid-loop does not redo already-finished work.
                var results = string.IsNullOrEmpty(working.LanguageResultsJson)
                    ? new List<LanguageResult>(options.TranslationTargetLanguages.Length)
                    : JsonSerializer.Deserialize<List<LanguageResult>>(working.LanguageResultsJson)!;

                var done = results.Select(r => r.Language)
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Speaker/gender labels are language-invariant (estimated once during
                // transcription), so read them once from the ORIGINAL vtt here rather than
                // per language. Empty when diarization was skipped — speakerVoices then
                // stays null for every language below, preserving today's single-voice
                // behaviour exactly.
                var originalVttContent = !string.IsNullOrEmpty(working.VttFilePath)
                    ? await _fs.ReadAllTextAsync(working.VttFilePath, ct)
                    : string.Empty;
                var speakerRows = SpeakerSampleExtractorService.ParseSpeakerRows(
                    VttTranslatorService.ExtractLeadingNote(originalVttContent));

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

                    var voiceName = options.UseFemaleVoice ? voice.FemaleVoices[0] : voice.MaleVoices[0];

                    // Assign each speaker their own voice from this language's gender pools,
                    // cycling when there are more same-gender speakers than distinct voices.
                    var speakerVoices = speakerRows.Count > 0
                        ? SpeakerVoiceAssigner.Assign(
                            speakerRows.Select(r => (r.Label, r.Gender)).ToList(),
                            voice.MaleVoices, voice.FemaleVoices, options.UseFemaleVoice)
                        : null;

                    _logger.LogInformation(
                        "Processing language: {Language} (default voice: {Voice}{SpeakerVoices})",
                        lang, voiceName,
                        speakerVoices is { Count: > 0 }
                            ? $"; per-speaker: {string.Join(", ", speakerVoices.Select(kv => $"{kv.Key}={kv.Value}"))}"
                            : "");

                    await _vttTranslator.TranslateAsync(working, lang, ct);
                    var translatedPath = working.TranslatedVttFilePath!;

                    await _azureTts.SynthesiseAsync(
                        working,
                        options.AzureSubscriptionKey,
                        options.AzureEndpointUrl,
                        voiceName,
                        voice.Lang,
                        speakerVoices,
                        ct);

                    await _audioMixer.MixAsync(working, options.FfmpegPath, ct);

                    results.Add(new LanguageResult(lang, working.MixedAudioPath!, translatedPath));

                    // Persist progress after every language. AudioMixerService already wrote
                    // MixedNoVoiceWithSyntheticVoice; override state back to TranslatingVtt
                    // until the final language is done so crash recovery (StablePredecessor
                    // TranslatingVtt → VoiceRemoved) restores the job correctly and the loop
                    // skips already-completed languages on the next run.
                    var allDone = options.TranslationTargetLanguages
                        .All(l => results.Any(r => string.Equals(r.Language, l, StringComparison.OrdinalIgnoreCase)));

                    working.LanguageResultsJson = JsonSerializer.Serialize(results);
                    working.State               = allDone
                        ? JobState.MixedNoVoiceWithSyntheticVoice
                        : JobState.TranslatingVtt;
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

    // Pauses right after subtitle extraction so a human can review the per-speaker gender
    // estimates written to job.VttFilePath's NOTE header (see vtt_common.py's pitch-threshold
    // heuristic, which can misclassify voices near the male/female boundary) and hand-correct
    // the NOTE line before it's baked into TTS voice assignment (see SpeakerVoiceAssigner)
    // downstream. Gated by PipelineOptions.PauseForGenderReview (default off).
    private async Task PauseForGenderReviewAsync(VideoJob job, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.VttFilePath)) return;

        var vttContent = await _fs.ReadAllTextAsync(job.VttFilePath, ct);
        var speakerRows = SpeakerSampleExtractorService.ParseSpeakerRows(VttTranslatorService.ExtractLeadingNote(vttContent));

        if (speakerRows.Count == 0)
        {
            _logger.LogInformation(
                "PauseForGenderReview is enabled but {Vtt} has no speaker/gender data — skipping pause.",
                job.VttFilePath);
            return;
        }

        _logger.LogInformation("Pausing for gender review — job {Id} ({File})", job.Id, job.OriginalFileName);

        var message = new StringBuilder()
            .AppendLine()
            .AppendLine($"=== Gender review: {job.OriginalFileName} ===")
            .AppendLine($"VTT: {job.VttFilePath}");
        foreach (var row in speakerRows)
            message.AppendLine($"  {row.Label}: {row.Gender} ({row.Start:hh\\:mm\\:ss}-{row.End:hh\\:mm\\:ss})");
        message.Append("Edit the NOTE header above in the VTT file now if any gender looks wrong, then press any key to continue...");

        await _userPrompt.WaitForKeyPressAsync(message.ToString(), ct);
    }
}
