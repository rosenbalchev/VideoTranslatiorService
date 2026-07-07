using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IAzureSpeechEngine
{
    Task<SpeechAudioResult> SpeakSsmlAsync(
        string ssml,
        string endpointUrl,
        string subscriptionKey,
        string voiceName,
        CancellationToken ct = default);
}
