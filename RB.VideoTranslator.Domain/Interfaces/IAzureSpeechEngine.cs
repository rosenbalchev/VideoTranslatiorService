namespace RB.VideoTranslator.Domain.Interfaces;

public interface IAzureSpeechEngine
{
    Task<byte[]> SpeakSsmlAsync(
        string ssml,
        string endpointUrl,
        string subscriptionKey,
        string voiceName,
        CancellationToken ct = default);
}
