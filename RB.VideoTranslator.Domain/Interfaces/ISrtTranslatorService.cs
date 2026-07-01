using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface ISrtTranslatorService
{
    /// <summary>
    /// Translates the extracted .srt file to the target language.
    /// Populates <see cref="VideoJob.TranslatedSrtFilePath"/> and transitions state to
    /// <see cref="JobState.SrtTranslated"/>.
    /// </summary>
    Task TranslateAsync(VideoJob job, string targetLanguage, CancellationToken ct = default);
}
