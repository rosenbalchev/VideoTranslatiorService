using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public interface IVideoMuxerService
{
    /// <summary>
    /// Muxes the mixed audio stream back into the silent video using ffmpeg.
    /// Populates <see cref="VideoJob.OutputFilePath"/> and transitions state to
    /// <see cref="JobState.AddedToOriginalVideo"/>.
    /// </summary>
    Task MuxAsync(VideoJob job, string ffmpegPath, string outputFolder, CancellationToken ct = default);
}
