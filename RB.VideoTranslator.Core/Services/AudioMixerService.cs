using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class AudioMixerService : IAudioMixerService
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _runner;
    private readonly IFileSystem _fs;
    private readonly ILogger<AudioMixerService> _logger;

    public AudioMixerService(
        IVideoJobRepository repo,
        IProcessRunner runner,
        IFileSystem fs,
        ILogger<AudioMixerService> logger)
    {
        _repo   = repo;
        _runner = runner;
        _fs     = fs;
        _logger = logger;
    }

    public async Task MixAsync(VideoJob job, string ffmpegPath = "ffmpeg", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.VoiceRemovedAudioPath))
            throw new InvalidOperationException($"Job {job.Id} has no VoiceRemovedAudioPath set.");
        if (string.IsNullOrEmpty(job.AzureTtsAudioPath))
            throw new InvalidOperationException($"Job {job.Id} has no AzureTtsAudioPath set.");
        if (string.IsNullOrEmpty(job.TranslatedSrtFilePath))
            throw new InvalidOperationException($"Job {job.Id} has no TranslatedSrtFilePath set.");

        // Derive base from the translated SRT so each language produces a distinct mixed file
        // (e.g. "video_translated_Bulgarian_mixed.wav" vs "video_translated_German_mixed.wav").
        var baseName  = Path.GetFileNameWithoutExtension(job.TranslatedSrtFilePath);
        var outputWav = Path.Combine(job.ProcessingFolderPath, $"{baseName}_mixed.wav");

        _logger.LogInformation(
            "Mixing {Bed} + {Voice} → {Out}",
            job.VoiceRemovedAudioPath, job.AzureTtsAudioPath, outputWav);

        // Cap at stereo: even for surround sources the dubbed track is at most stereo,
        // so downmix to match rather than produce an asymmetric mix.
        var ac = Math.Clamp(job.AudioChannels, 1, 2);

        await _runner.RunAsync(
            ffmpegPath,
            $"-y -i \"{job.VoiceRemovedAudioPath}\" -i \"{job.AzureTtsAudioPath}\" " +
            $"-filter_complex \"amix=inputs=2:duration=longest:normalize=0\" -ac {ac} \"{outputWav}\"",
            ct);

        if (!_fs.FileExists(outputWav))
            throw new FileNotFoundException($"ffmpeg did not produce mixed audio at {outputWav}");

        job.MixedAudioPath = outputWav;
        job.State          = JobState.MixedNoVoiceWithSyntheticVoice;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Mixed audio written to {Path}", outputWav);
    }
}
