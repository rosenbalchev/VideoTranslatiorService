using Microsoft.CognitiveServices.Speech;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class AzureSpeechEngine : IAzureSpeechEngine
{
    public async Task<byte[]> SpeakSsmlAsync(
        string ssml,
        string endpointUrl,
        string subscriptionKey,
        string voiceName,
        CancellationToken ct = default)
    {
        var speechConfig = SpeechConfig.FromEndpoint(new Uri(endpointUrl), subscriptionKey);
        speechConfig.SpeechSynthesisVoiceName = voiceName;
        speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff44100Hz16BitMonoPcm);

        // null AudioConfig → audio is captured in-memory (no speaker playback)
        using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig: null);
        using var result = await synthesizer.SpeakSsmlAsync(ssml);

        if (result.Reason == ResultReason.SynthesizingAudioCompleted)
            return result.AudioData;

        var details = SpeechSynthesisCancellationDetails.FromResult(result);
        throw new InvalidOperationException(
            $"Azure TTS cancelled ({details.Reason}): {details.ErrorDetails}");
    }
}
