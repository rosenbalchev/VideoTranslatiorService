using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IVttToAzureTtsService
{
    /// <summary>
    /// Synthesises speech from <see cref="VideoJob.TranslatedVttFilePath"/> using Azure TTS.
    /// Populates <see cref="VideoJob.AzureTtsAudioPath"/> and transitions state to
    /// <see cref="JobState.AzureTtsSynthesised"/>.
    /// </summary>
    /// <param name="speakerVoices">
    /// Optional map of speaker label (e.g. "Speaker1", matching the VTT's per-cue NOTE
    /// labels) to Azure voice name. When a cue's speaker has an entry here, it overrides
    /// <paramref name="voiceName"/> for that cue only — giving each speaker a distinct
    /// voice. Pass <see langword="null"/> (or omit) to use <paramref name="voiceName"/> for
    /// every cue, e.g. when diarization produced no speaker data.
    /// </param>
    Task SynthesiseAsync(
        VideoJob job,
        string subscriptionKey,
        string endpointUrl,
        string voiceName = "en-US-Ava:DragonHDLatestNeural",
        string lang = "en-US",
        IReadOnlyDictionary<string, string>? speakerVoices = null,
        CancellationToken ct = default);
}
