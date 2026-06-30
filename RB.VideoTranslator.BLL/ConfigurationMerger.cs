namespace RB.VideoTranslator.BLL;

/// <summary>
/// Helper for merging CLI argument overrides into pipeline options.
/// Used by CLI to apply command-line arguments on top of file-based configuration.
/// </summary>
public static class ConfigurationMerger
{
    /// <summary>
    /// Applies CLI-provided option overrides to the pipeline options.
    /// Only sets properties when CLI values are explicitly provided (not null).
    /// </summary>
    /// <param name="options">The target options object to modify.</param>
    /// <param name="workFolder">CLI --work-folder argument.</param>
    /// <param name="ffmpegPath">CLI --ffmpeg argument.</param>
    /// <param name="pythonPath">CLI --python argument.</param>
    /// <param name="demucsPath">CLI --demucs argument.</param>
    /// <param name="azureKey">CLI --azure-key argument.</param>
    /// <param name="azureEndpoint">CLI --azure-endpoint argument.</param>
    /// <param name="openAiEndpoint">CLI --openai-endpoint argument.</param>
    /// <param name="openAiDeployment">CLI --openai-deployment argument.</param>
    /// <param name="targetLanguages">CLI --target-lang argument.</param>
    /// <param name="venvPath">CLI --venv argument.</param>
    /// <param name="useFemaleVoice">CLI --female flag.</param>
    public static void MergeCliOptions(
        PipelineOptions options,
        string? workFolder = null,
        string? ffmpegPath = null,
        string? pythonPath = null,
        string? demucsPath = null,
        string? azureKey = null,
        string? azureEndpoint = null,
        string? openAiEndpoint = null,
        string? openAiDeployment = null,
        string? targetLanguages = null,
        string? venvPath = null,
        bool? useFemaleVoice = null)
    {
        if (!string.IsNullOrEmpty(workFolder))
            options.WorkingFolderPath = workFolder;

        if (!string.IsNullOrEmpty(azureKey))
            options.AzureSubscriptionKey = azureKey;

        if (!string.IsNullOrEmpty(azureEndpoint))
            options.AzureEndpointUrl = azureEndpoint;

        if (!string.IsNullOrEmpty(openAiEndpoint))
            options.AzureOpenAiEndpoint = NormalizeOpenAiEndpoint(openAiEndpoint);

        if (!string.IsNullOrEmpty(ffmpegPath))
            options.FfmpegPath = ffmpegPath;

        if (!string.IsNullOrEmpty(openAiDeployment))
            options.AzureOpenAiDeployment = openAiDeployment;

        if (!string.IsNullOrEmpty(targetLanguages))
            options.TranslationTargetLanguages = targetLanguages
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (useFemaleVoice.HasValue)
            options.UseFemaleVoice = useFemaleVoice.Value;

        // Venv: explicit CLI venv arg > config VenvPath > default <WorkingFolderPath>\rb.video.translator
        if (!string.IsNullOrEmpty(venvPath))
        {
            options.VenvPath = venvPath;
        }
        else if (string.IsNullOrEmpty(options.VenvPath) && !string.IsNullOrEmpty(options.WorkingFolderPath))
        {
            var defaultVenv = Path.Combine(options.WorkingFolderPath, "rb.video.translator");
            if (Directory.Exists(defaultVenv))
                options.VenvPath = defaultVenv;
        }

        // Python/Demucs: CLI args > config > venv path
        if (!string.IsNullOrEmpty(options.VenvPath))
        {
            var venvPythonPath = GetVenvPythonPath(options.VenvPath);

            // Only override if not explicitly set by user CLI args
            if (!string.IsNullOrEmpty(pythonPath))
                options.PythonPath = pythonPath;
            else if (string.IsNullOrEmpty(options.PythonPath) || options.PythonPath == "python")
                options.PythonPath = venvPythonPath;

            if (!string.IsNullOrEmpty(demucsPath))
                options.DemucsPath = demucsPath;
            else if (string.IsNullOrEmpty(options.DemucsPath) || options.DemucsPath == "python")
                options.DemucsPath = venvPythonPath;
        }
        else
        {
            if (!string.IsNullOrEmpty(pythonPath))
                options.PythonPath = pythonPath;
            if (!string.IsNullOrEmpty(demucsPath))
                options.DemucsPath = demucsPath;
        }
    }

    /// <summary>
    /// Gets the Python executable path within a virtual environment.
    /// </summary>
    private static string GetVenvPythonPath(string venvRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(venvRoot, "Scripts", "python.exe")
            : Path.Combine(venvRoot, "bin", "python");

    /// <summary>
    /// AzureOpenAIClient builds the REST path internally (/openai/deployments/…).
    /// Strip any /openai/v1 suffix the user may have copied from AI Foundry docs.
    /// </summary>
    private static string NormalizeOpenAiEndpoint(string raw)
    {
        var url = raw.TrimEnd('/');
        foreach (var suffix in new[] { "/openai/v1", "/openai" })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return url[..^suffix.Length] + "/";
        }
        return url + "/";
    }
}
