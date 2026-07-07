namespace RB.VideoTranslator.Domain.Models;

/// <summary>
/// Synthesised audio plus the WAV format actually returned by the speech engine
/// (parsed once from the header), so callers never need to inspect raw bytes.
/// </summary>
public sealed record SpeechAudioResult(
    byte[] AudioData,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int DurationMs);
