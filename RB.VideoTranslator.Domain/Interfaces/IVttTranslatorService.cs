using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IVttTranslatorService
{
    /// <summary>
    /// Translates the extracted .vtt file to the target language.
    /// Populates <see cref="VideoJob.TranslatedVttFilePath"/> and transitions state to
    /// <see cref="JobState.VttTranslated"/>.
    /// </summary>
    Task TranslateAsync(VideoJob job, string targetLanguage, CancellationToken ct = default);
}
