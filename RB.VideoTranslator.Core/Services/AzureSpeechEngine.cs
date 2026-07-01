using Microsoft.CognitiveServices.Speech;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Core.Services;

public sealed class AzureSpeechEngine : IAzureSpeechEngine
{
    public async Task<SpeechAudioResult> SpeakSsmlAsync(
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
            return ParseWav(result.AudioData, (int)result.AudioDuration.TotalMilliseconds);

        var details = SpeechSynthesisCancellationDetails.FromResult(result);
        throw new InvalidOperationException(
            $"Azure TTS cancelled ({details.Reason}): {details.ErrorDetails}");
    }

    // Reads channel count, sample rate, and bit depth straight from the WAV header so
    // callers (SrtToAzureTtsService) never need to inspect the raw bytes themselves.
    // Duration comes directly from Azure's own SpeechSynthesisResult.AudioDuration —
    // no need to recompute it from the PCM byte count.
    private static SpeechAudioResult ParseWav(byte[] wav, int durationMs)
    {
        if (!IsValidRiffWav(wav))
            throw new InvalidOperationException("Azure TTS returned audio that is not a valid RIFF/WAVE file.");

        return new SpeechAudioResult(
            AudioData: wav,
            SampleRate: (int)BitConverter.ToUInt32(wav, 24),
            Channels: BitConverter.ToUInt16(wav, 22),
            BitsPerSample: BitConverter.ToUInt16(wav, 34),
            DurationMs: durationMs);
    }

    private static bool IsValidRiffWav(byte[] data) =>
        data.Length >= 12
        && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
        && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E';
}
