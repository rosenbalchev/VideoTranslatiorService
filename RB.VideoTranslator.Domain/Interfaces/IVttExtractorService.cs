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
    /// <param name="useOnnxTranscription">
    /// When true, transcribes via <see cref="IWhisperOnnxTranscriberService"/> (native C#,
    /// ONNX-exported Whisper model) instead of <c>tool_wavToVttVoiceMark.py</c>'s
    /// WhisperX/CTranslate2 path. If <paramref name="enableVoiceMarks"/> is also true, the
    /// resulting segments are diarized by <c>tool_diarizeVtt.py</c> (whisperx/pyannote/torch,
    /// unaffected either way).
    /// </param>
    /// <param name="onnxModelPath">
    /// Path to the ONNX-exported Whisper model directory. Required when
    /// <paramref name="useOnnxTranscription"/> is true.
    /// </param>
    /// <param name="ffmpegPath">
    /// ffmpeg executable, used only when <paramref name="useOnnxTranscription"/> is true to
    /// normalize the extracted audio to 16kHz mono before running inference.
    /// </param>
    Task ExtractAsync(
        VideoJob job,
        string pythonPath = "python",
        bool enableVoiceMarks = true,
        bool useOnnxTranscription = false,
        string? onnxModelPath = null,
        string ffmpegPath = "ffmpeg",
        CancellationToken ct = default);
}
