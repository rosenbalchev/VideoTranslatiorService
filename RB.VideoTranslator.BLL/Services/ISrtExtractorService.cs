using RB.VideoTranslator.Data.Entities;

namespace RB.VideoTranslator.BLL.Services;

public interface ISrtExtractorService
{
    /// <summary>
    /// Transcribes the extracted WAV audio to a unicode .srt subtitle file using
    /// the local Whisper model via <c>tool_wavToSrt.py</c>.
    /// Populates <see cref="VideoJob.SrtFilePath"/> and transitions state to
    /// <see cref="JobState.SrtExtracted"/>.
    /// </summary>
    Task ExtractAsync(VideoJob job, string pythonPath = "python", CancellationToken ct = default);
}
