using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class MediaSeparatorService : IMediaSeparatorService
{
    private readonly IVideoJobRepository _repo;
    private readonly ILogger<MediaSeparatorService> _logger;
    private readonly IProcessRunner _processRunner;

    public MediaSeparatorService(
        IVideoJobRepository repo,
        ILogger<MediaSeparatorService> logger,
        IProcessRunner processRunner)
    {
        _repo = repo;
        _logger = logger;
        _processRunner = processRunner;
    }

    public async Task<(string AudioPath, string SilentVideoPath)> SeparateAsync(
        VideoJob job,
        string ffmpegPath = "ffmpeg",
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.ProcessingVideoPath))
            throw new InvalidOperationException($"Job {job.Id} has no ProcessingVideoPath set.");

        var baseName = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var audioPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}_audio.wav");
        var silentVideoPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}_silent.mp4");

        _logger.LogInformation("Extracting audio track from {Source}", job.ProcessingVideoPath);
        await _processRunner.RunAsync(
            ffmpegPath,
            $"-y -i \"{job.ProcessingVideoPath}\" -vn -acodec pcm_s16le \"{audioPath}\"",
            ct);

        _logger.LogInformation("Stripping audio from video {Source}", job.ProcessingVideoPath);
        await _processRunner.RunAsync(
            ffmpegPath,
            $"-y -i \"{job.ProcessingVideoPath}\" -an -vcodec copy \"{silentVideoPath}\"",
            ct);

        job.ExtractedAudioPath = audioPath;
        job.SilentVideoPath = silentVideoPath;
        job.State = JobState.AudioExtracted;
        await _repo.UpdateAsync(job, ct);

        return (audioPath, silentVideoPath);
    }
}
