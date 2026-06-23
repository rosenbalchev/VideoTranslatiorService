using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public interface IVideoMuxerService
{
    /// <summary>
    /// Muxes the silent video with the original audio and all per-language dubbed audio tracks.
    /// Each language also gets an embedded subtitle track. Populates
    /// <see cref="VideoJob.OutputFilePath"/> and transitions state to
    /// <see cref="JobState.AddedToOriginalVideo"/>.
    /// </summary>
    Task MuxAsync(
        VideoJob job,
        string ffmpegPath,
        string outputFolder,
        IReadOnlyList<LanguageResult> languageResults,
        CancellationToken ct = default);
}
