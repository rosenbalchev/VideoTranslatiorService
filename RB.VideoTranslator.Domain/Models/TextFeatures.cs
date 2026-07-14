namespace RB.VideoTranslator.Domain.Models;

// Feature vector extracted from one subtitle paragraph for voice-pace prediction
// (see RB.VideoTranslator.Core.Services.TextFeatureExtractor / VoicePaceModel).
public readonly record struct TextFeatures(int CharCount, int WordCount, int SentenceCount, int CommaCount);
