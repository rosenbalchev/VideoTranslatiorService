namespace VideoTranslatorService.BLL;

public sealed record PipelineOptions
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string PythonPath { get; init; } = "python";
    public string DemucsPath { get; init; } = "python";
    public string AzureSubscriptionKey { get; init; } = string.Empty;
    public string AzureEndpointUrl { get; init; } = string.Empty;
    public string AzureOpenAiEndpoint { get; init; } = string.Empty;
    public string AzureOpenAiDeployment { get; init; } = "gpt-4o-mini";
    public string[] TranslationTargetLanguages { get; init; } = ["Bulgarian"];
    public string OutputFolderPath { get; init; } = "output";
}
