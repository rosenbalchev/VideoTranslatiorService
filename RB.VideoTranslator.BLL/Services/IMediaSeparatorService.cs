using RB.VideoTranslator.Data.Entities;

namespace RB.VideoTranslator.BLL.Services;

public interface IMediaSeparatorService
{
    /// <summary>
    /// Extracts audio and produces a silent video copy using ffmpeg.
    /// Updates the job record and transitions state to <see cref="JobState.AudioExtracted"/>.
    /// </summary>
    Task<(string AudioPath, string SilentVideoPath)> SeparateAsync(
        VideoJob job,
        string ffmpegPath = "ffmpeg",
        CancellationToken ct = default);
}
