using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Domain.Interfaces;

/// <summary>
/// Native C# Whisper ONNX transcription (mel spectrogram + BPE tokenizer + autoregressive
/// decode loop, all in RB.VideoTranslator.WhisperOnnx), replacing the now-deleted Python
/// tool_wavToVtt_onnx.py path. Currently tries DirectML GPU first, falling back to CPU
/// in-process (see WhisperOnnxTranscriberService.cs); NPU support via a separate
/// OpenVINO-based process is a planned follow-up, not implemented yet. Diarization is out of
/// scope here — it has no .NET equivalent and stays in Python (tool_diarizeVtt.py), called
/// separately by VttExtractorService with the segments this method returns.
/// </summary>
public interface IWhisperOnnxTranscriberService
{
    /// <param name="wavPath">Path to the extracted audio (any sample rate/channel count — this
    /// service normalizes to 16kHz mono via ffmpeg before running inference).</param>
    /// <param name="onnxModelDir">Path to the ONNX-exported Whisper model directory.</param>
    /// <param name="ffmpegPath">ffmpeg executable used for the 16kHz mono normalization step.</param>
    Task<WhisperTranscription> TranscribeAsync(
        string wavPath,
        string onnxModelDir,
        string ffmpegPath,
        CancellationToken ct = default);
}
