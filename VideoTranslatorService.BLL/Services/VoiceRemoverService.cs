using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class VoiceRemoverService : IVoiceRemoverService
{
    private const string DemucsModel = "htdemucs";

    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly ILogger<VoiceRemoverService> _logger;

    public VoiceRemoverService(
        IVideoJobRepository repo,
        IProcessRunner processRunner,
        IFileSystem fs,
        ILogger<VoiceRemoverService> logger)
    {
        _repo = repo;
        _processRunner = processRunner;
        _fs = fs;
        _logger = logger;
    }

    public async Task RemoveAsync(VideoJob job, string demucsPath = "demucs", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.ExtractedAudioPath))
            throw new InvalidOperationException($"Job {job.Id} has no ExtractedAudioPath set.");

        var demucsOutDir = Path.Combine(job.ProcessingFolderPath, "demucs");
        var audioBaseName = Path.GetFileNameWithoutExtension(job.ExtractedAudioPath);
        var noVocalsPath = Path.Combine(demucsOutDir, DemucsModel, audioBaseName, "no_vocals.flac");

        await ValidateCudaAsync(demucsPath, ct);

        _logger.LogInformation(
            "Removing voice from {Audio} → {NoVocals} (this may take a while)",
            job.ExtractedAudioPath, noVocalsPath);

        await _processRunner.RunAsync(
            demucsPath,
            $"-m demucs --two-stems=vocals --flac --device cuda --out \"{demucsOutDir}\" \"{job.ExtractedAudioPath}\"",
            ct);
        if (!_fs.FileExists(noVocalsPath))
            throw new FileNotFoundException($"Demucs did not produce expected output: {noVocalsPath}");

        job.VoiceRemovedAudioPath = noVocalsPath;
        job.State = JobState.VoiceRemoved;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Music bed written to {NoVocals}", noVocalsPath);
    }

    private async Task ValidateCudaAsync(string pythonPath, CancellationToken ct)
    {
        _logger.LogInformation("Checking CUDA availability...");

        var output = await _processRunner.RunAndCaptureAsync(
            pythonPath,
            "-c \"import torch; print(torch.cuda.is_available())\"",
            ct);

        if (!output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "CUDA is not available. Demucs requires a CUDA-capable GPU — " +
                "ensure PyTorch with CUDA support is installed (see README).");

        _logger.LogInformation("CUDA is available — Demucs will run on GPU");
    }
}
