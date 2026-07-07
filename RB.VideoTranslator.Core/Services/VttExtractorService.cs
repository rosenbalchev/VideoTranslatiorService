using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class VttExtractorService : IVttExtractorService
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly ILogger<VttExtractorService> _logger;

    public VttExtractorService(
        IVideoJobRepository repo,
        IProcessRunner processRunner,
        IFileSystem fs,
        ILogger<VttExtractorService> logger)
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

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "tool_wavToVtt_GPU.py");
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Whisper tool not found: {scriptPath}");

        var baseName = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var vttPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}.vtt");

        _logger.LogInformation(
            "Transcribing {Audio} → {Vtt} (this may take a while)",
            job.ExtractedAudioPath, vttPath);

        await _processRunner.RunAsync(
            pythonPath,
            $"\"{scriptPath}\" \"{job.ExtractedAudioPath}\" \"{vttPath}\"",
            ct);
        if (!_fs.FileExists(vttPath))
            throw new FileNotFoundException($"Whisper did not produce expected VTT output: {vttPath}");

        job.VttFilePath = vttPath;
        job.State = JobState.VttExtracted;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("VTT written to {Vtt}", vttPath);
    }
}
