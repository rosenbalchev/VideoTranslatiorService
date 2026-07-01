namespace RB.VideoTranslator.Domain.Models;

public sealed record LanguageResult(
    string Language,
    string MixedAudioPath,
    string TranslatedSrtFilePath);
