using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class VideoMuxerService : IVideoMuxerService
{
    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _runner;
    private readonly IFileSystem _fs;
    private readonly ILogger<VideoMuxerService> _logger;

    public VideoMuxerService(
        IVideoJobRepository repo,
        IProcessRunner runner,
        IFileSystem fs,
        ILogger<VideoMuxerService> logger)
    {
        _repo   = repo;
        _runner = runner;
        _fs     = fs;
        _logger = logger;
    }

    public async Task MuxAsync(
        VideoJob job,
        string ffmpegPath,
        string outputFolder,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.SilentVideoPath))
            throw new InvalidOperationException($"Job {job.Id} has no SilentVideoPath set.");
        if (string.IsNullOrEmpty(job.MixedAudioPath))
            throw new InvalidOperationException($"Job {job.Id} has no MixedAudioPath set.");
        if (string.IsNullOrEmpty(job.TranslatedSrtFilePath))
            throw new InvalidOperationException($"Job {job.Id} has no TranslatedSrtFilePath set.");

        Directory.CreateDirectory(outputFolder);

        var outputPath = Path.Combine(outputFolder, job.OriginalFileName);

        // mov_text is required for soft subtitles in MP4; MKV supports srt directly.
        var ext            = Path.GetExtension(job.OriginalFileName).ToLowerInvariant();
        var subtitleCodec  = ext is ".mkv" ? "srt" : "mov_text";

        _logger.LogInformation(
            "Muxing {Video} + {Audio} + {Srt} → {Out} (subtitle codec: {Codec})",
            job.SilentVideoPath, job.MixedAudioPath, job.TranslatedSrtFilePath, outputPath, subtitleCodec);

        await _runner.RunAsync(
            ffmpegPath,
            $"-y -i \"{job.SilentVideoPath}\" -i \"{job.MixedAudioPath}\" -i \"{job.TranslatedSrtFilePath}\" " +
            $"-map 0:v:0 -map 1:a:0 -map 2:s:0 " +
            $"-c:v copy -c:a aac -b:a 192k -c:s {subtitleCodec} -shortest \"{outputPath}\"",
            ct);

        if (!_fs.FileExists(outputPath))
            throw new FileNotFoundException($"ffmpeg did not produce output video at {outputPath}");

        job.OutputFilePath = outputPath;
        job.State          = JobState.AddedToOriginalVideo;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Output video written to {Path}", outputPath);
    }
}
