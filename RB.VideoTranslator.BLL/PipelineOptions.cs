namespace RB.VideoTranslator.BLL;

/// <summary>
/// Configuration section name used in appsettings.json.
/// </summary>
public static class PipelineOptionsDefaults
{
    public const string SectionName = "RBVideoTranslator";
}

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
    public string AzureSubscriptionKey { get; set; } = string.Empty;
    public string AzureEndpointUrl { get; set; } = string.Empty;
    public string AzureOpenAiEndpoint { get; set; } = string.Empty;
    public string AzureOpenAiDeployment { get; set; } = "gpt-4o-mini";
    public string[] TranslationTargetLanguages { get; set; } = ["Bulgarian"];
    public string OutputFolderPath { get; set; } = string.Empty;
    public bool UseFemaleVoice { get; set; } = false;
}
