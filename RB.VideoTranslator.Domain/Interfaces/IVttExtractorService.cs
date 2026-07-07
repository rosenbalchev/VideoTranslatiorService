using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IVttExtractorService
{
    /// <summary>
    /// Transcribes the extracted WAV audio to a unicode .vtt subtitle file using
    /// the local Whisper model via <c>tool_wavToVtt.py</c>.
    /// Populates <see cref="VideoJob.VttFilePath"/> and transitions state to
    /// <see cref="JobState.VttExtracted"/>.
    /// </summary>
    Task ExtractAsync(VideoJob job, string pythonPath = "python", CancellationToken ct = default);
}
