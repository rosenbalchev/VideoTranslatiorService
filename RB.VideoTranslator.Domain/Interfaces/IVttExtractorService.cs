using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IVttExtractorService
{
    /// <summary>
    /// Transcribes the extracted WAV audio to a unicode .vtt subtitle file using
    /// the local Whisper model via <c>tool_wavToVttVoiceMark.py</c>.
    /// Populates <see cref="VideoJob.VttFilePath"/> and transitions state to
    /// <see cref="JobState.VttExtracted"/>.
    /// </summary>
    /// <param name="enableVoiceMarks">
    /// When false, passes --no-voice-marks so the script skips speaker diarization and
    /// gender estimation and transcribes only.
    /// </param>
    Task ExtractAsync(
        VideoJob job,
        string pythonPath = "python",
        bool enableVoiceMarks = true,
        CancellationToken ct = default);
}
