namespace RB.VideoTranslator.Domain.Models;

public sealed record TranscribedSegment(double Start, double End, string Text);

public sealed record WhisperTranscription(IReadOnlyList<TranscribedSegment> Segments, string Language);
