using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class MediaSeparatorService : IMediaSeparatorService
{
    private readonly IVideoJobRepository _repo;
    private readonly ILogger<MediaSeparatorService> _logger;

    public MediaSeparatorService(IVideoJobRepository repo, ILogger<MediaSeparatorService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<(string AudioPath, string SilentVideoPath)> SeparateAsync(
        VideoJob job,
        string ffmpegPath = "ffmpeg",
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.ProcessingVideoPath))
            throw new InvalidOperationException($"Job {job.Id} has no ProcessingVideoPath set.");

        var baseName = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var audioPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}_audio.aac");
        var silentVideoPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}_silent.mp4");

        _logger.LogInformation("Extracting audio track from {Source}", job.ProcessingVideoPath);
        await RunFfmpegAsync(
            ffmpegPath,
            $"-y -i \"{job.ProcessingVideoPath}\" -vn -acodec copy \"{audioPath}\"",
            ct);

        _logger.LogInformation("Stripping audio from video {Source}", job.ProcessingVideoPath);
        await RunFfmpegAsync(
            ffmpegPath,
            $"-y -i \"{job.ProcessingVideoPath}\" -an -vcodec copy \"{silentVideoPath}\"",
            ct);

        job.ExtractedAudioPath = audioPath;
        job.SilentVideoPath = silentVideoPath;
        job.State = JobState.AudioExtracted;
        await _repo.UpdateAsync(job, ct);

        return (audioPath, silentVideoPath);
    }

    private async Task RunFfmpegAsync(string ffmpegPath, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        // Read stderr concurrently to prevent the buffer from blocking the process.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("ffmpeg stderr:\n{Stderr}", stderr);
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}. See log for details.");
        }
    }
}
