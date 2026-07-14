namespace RB.VideoTranslator.WhisperOnnx;

public sealed record WhisperSegment(double Start, double End, string Text);

public sealed record WhisperTranscriptionResult(IReadOnlyList<WhisperSegment> Segments, string Language);
