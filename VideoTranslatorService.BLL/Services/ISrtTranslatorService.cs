using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public interface ISrtTranslatorService
{
    /// <summary>
    /// Translates the extracted .srt file to the target language.
    /// Populates <see cref="VideoJob.TranslatedSrtFilePath"/> and transitions state to
    /// <see cref="JobState.SrtTranslated"/>.
    /// </summary>
    Task TranslateAsync(VideoJob job, string targetLanguage, CancellationToken ct = default);
}
