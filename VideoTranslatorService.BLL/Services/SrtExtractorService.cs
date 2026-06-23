using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class SrtExtractorService : ISrtExtractorService
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly ILogger<SrtExtractorService> _logger;

    public SrtExtractorService(
        IVideoJobRepository repo,
        IProcessRunner processRunner,
        IFileSystem fs,
        ILogger<SrtExtractorService> logger)
    {
        _repo = repo;
        _processRunner = processRunner;
        _fs = fs;
        _logger = logger;
    }

    public async Task ExtractAsync(VideoJob job, string pythonPath = "python", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.ExtractedAudioPath))
            throw new InvalidOperationException($"Job {job.Id} has no ExtractedAudioPath set.");

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "tool_wavToSrt_GPU.py");
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Whisper tool not found: {scriptPath}");

        var baseName = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var srtPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}.srt");

        _logger.LogInformation(
            "Transcribing {Audio} → {Srt} (this may take a while)",
            job.ExtractedAudioPath, srtPath);

        await _processRunner.RunAsync(
            pythonPath,
            $"\"{scriptPath}\" \"{job.ExtractedAudioPath}\" \"{srtPath}\"",
            ct);
        if (!_fs.FileExists(srtPath))
            throw new FileNotFoundException($"Whisper did not produce expected SRT output: {srtPath}");

        job.SrtFilePath = srtPath;
        job.State = JobState.SrtExtracted;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("SRT written to {Srt}", srtPath);
    }
}
