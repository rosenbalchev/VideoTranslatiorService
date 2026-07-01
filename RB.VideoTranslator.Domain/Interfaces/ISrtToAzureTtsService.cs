using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface ISrtToAzureTtsService
{
    /// <summary>
    /// Synthesises speech from <see cref="VideoJob.SrtFilePath"/> using Azure TTS.
    /// Populates <see cref="VideoJob.AzureTtsAudioPath"/> and transitions state to
    /// <see cref="JobState.AzureTtsSynthesised"/>.
    /// </summary>
    Task SynthesiseAsync(
        VideoJob job,
        string subscriptionKey,
        string endpointUrl,
        string voiceName = "en-US-Ava:DragonHDLatestNeural",
        string lang = "en-US",
        CancellationToken ct = default);
}
