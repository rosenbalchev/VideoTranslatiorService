namespace RB.VideoTranslator.Domain.Models;

/// <summary>
/// Pipeline runtime configuration. Bind via IOptions&lt;PipelineOptions&gt; or supply
/// programmatically. Settable properties are required by the IOptions pattern.
/// </summary>
public sealed class PipelineOptions
{
    /// <summary>Root folder for all pipeline data (input, processing, output, DB).</summary>
    public string WorkingFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the Python virtual environment created by an install script.
    /// When set, its interpreter is used for Whisper and Demucs instead of PythonPath/DemucsPath.
    /// Defaults to &lt;WorkingFolderPath&gt;\bgtts-env when empty.
    /// </summary>
    public string VenvPath { get; set; } = string.Empty;

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string PythonPath { get; set; } = "python";
    public string DemucsPath { get; set; } = "python";

    /// <summary>
    /// HuggingFace access token for the gated pyannote speaker-diarization models used by
    /// tool_wavToVttVoiceMark.py. An install script logs this into the venv's HuggingFace cache
    /// (via <c>huggingface-cli login</c>) so it does not need to be set as an env var at runtime.
    /// </summary>
    public string HfToken { get; set; } = string.Empty;

    /// <summary>
    /// Whether tool_wavToVttVoiceMark.py should run speaker diarization and gender estimation
    /// ("voice marks": the per-speaker NOTE header and per-cue speaker labels in the VTT).
    /// Requires <see cref="HfToken"/>. When false, the script transcribes only, matching the
    /// tool's pre-diarization behaviour.
    /// </summary>
    public bool EnableVoiceMarks { get; set; } = true;

    /// <summary>
    /// When true, VttExtractorService transcribes via IWhisperOnnxTranscriberService (native
    /// C#, RB.VideoTranslator.WhisperOnnx: DirectML GPU falling back to CPU) instead of
    /// shelling out to tool_wavToVttVoiceMark.py's WhisperX/CTranslate2 path. If voice marks
    /// are enabled, the resulting segments are handed to tool_diarizeVtt.py for diarization
    /// (whisperx/pyannote/torch — unaffected either way, no ONNX/C# equivalent exists).
    /// Requires the model produced by scripts/export_onnx_models.bat or .sh, expected at
    /// &lt;WorkingFolderPath&gt;\onnx-models\whisper-medium.
    /// </summary>
    public bool UseOnnxTranscription { get; set; } = true;
    public string AzureSubscriptionKey { get; set; } = string.Empty;
    public string AzureEndpointUrl { get; set; } = string.Empty;
    public string AzureOpenAiEndpoint { get; set; } = string.Empty;
    public string AzureOpenAiDeployment { get; set; } = "gpt-4o-mini";
    public string[] TranslationTargetLanguages { get; set; } = ["Bulgarian"];
    public string OutputFolderPath { get; set; } = string.Empty;
    public bool UseFemaleVoice { get; set; } = false;
}
