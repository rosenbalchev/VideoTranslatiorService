using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

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
        var vocalsPath = Path.Combine(demucsOutDir, DemucsModel, audioBaseName, "vocals.flac");

        var device = await DetectDeviceAsync(demucsPath, ct);

        _logger.LogInformation(
            "Removing voice from {Audio} → {NoVocals} (device={Device}, this may take a while)",
            job.ExtractedAudioPath, noVocalsPath, device);

        await _processRunner.RunAsync(
            demucsPath,
            $"-m demucs --two-stems=vocals --flac --device {device} --out \"{demucsOutDir}\" \"{job.ExtractedAudioPath}\"",
            ct);
        if (!_fs.FileExists(noVocalsPath))
            throw new FileNotFoundException($"Demucs did not produce expected output: {noVocalsPath}");
        if (!_fs.FileExists(vocalsPath))
            throw new FileNotFoundException($"Demucs did not produce expected output: {vocalsPath}");

        job.VoiceRemovedAudioPath = noVocalsPath;
        job.VocalsAudioPath = vocalsPath;
        job.State = JobState.VoiceRemoved;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Music bed written to {NoVocals}, isolated vocals written to {Vocals}", noVocalsPath, vocalsPath);
    }

    // Demucs' htdemucs model (used here) has known incompatibilities with PyTorch's MPS
    // backend — it relies on complex-valued tensors and custom ops that don't reliably
    // work there — so unlike whisperx/tool_wavToVttVoiceMark.py's CUDA-or-CPU device
    // selection, we deliberately don't auto-select "mps" on Apple Silicon even though
    // it's technically available. CPU is the safe default anywhere CUDA isn't.
    // https://github.com/facebookresearch/demucs/issues/435
    private async Task<string> DetectDeviceAsync(string pythonPath, CancellationToken ct)
    {
        _logger.LogInformation("Checking CUDA availability for Demucs...");

        var output = await _processRunner.RunAndCaptureAsync(
            pythonPath,
            "-c \"import torch; print(torch.cuda.is_available())\"",
            ct);

        if (output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("CUDA is available — Demucs will run on GPU");
            return "cuda";
        }

        _logger.LogInformation("CUDA not available — Demucs will run on CPU (slower)");
        return "cpu";
    }
}
