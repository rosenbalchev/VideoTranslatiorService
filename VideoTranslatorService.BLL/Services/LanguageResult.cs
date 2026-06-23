namespace VideoTranslatorService.BLL.Services;

public sealed record LanguageResult(
    string Language,
    string MixedAudioPath,
    string TranslatedSrtFilePath);
