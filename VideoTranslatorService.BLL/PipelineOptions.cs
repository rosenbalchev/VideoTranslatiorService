namespace VideoTranslatorService.BLL;

public sealed record PipelineOptions
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string PythonPath { get; init; } = "python";
}
