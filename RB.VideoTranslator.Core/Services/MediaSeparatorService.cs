using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class MediaSeparatorService : IMediaSeparatorService
{
    private readonly IVideoJobRepository _repo;
    private readonly ILogger<MediaSeparatorService> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;

    public MediaSeparatorService(
        IVideoJobRepository repo,
        ILogger<MediaSeparatorService> logger,
        IProcessRunner processRunner,
        IFileSystem fs)
    {
        _repo = repo;
        _logger = logger;
        _processRunner = processRunner;
        _fs = fs;
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
        if (!_fs.FileExists(audioPath))
            throw new FileNotFoundException($"ffmpeg did not produce expected audio output: {audioPath}");

        // Probe the PCM WAV header to capture the original audio format.
        // offset 22 = NumChannels (uint16), offset 24 = SampleRate (uint32).
        var header = new byte[36];
        await using (var wav = _fs.OpenRead(audioPath))
            await wav.ReadExactlyAsync(header, ct);

        job.AudioChannels   = BitConverter.ToUInt16(header, 22);
        job.AudioSampleRate = (int)BitConverter.ToUInt32(header, 24);
        _logger.LogInformation(
            "Input audio: {Ch}ch @ {Rate}Hz",
            job.AudioChannels, job.AudioSampleRate);

        _logger.LogInformation("Stripping audio from video {Source}", job.ProcessingVideoPath);
        await _processRunner.RunAsync(
            ffmpegPath,
            $"-y -i \"{job.ProcessingVideoPath}\" -an -vcodec copy \"{silentVideoPath}\"",
            ct);
        if (!_fs.FileExists(silentVideoPath))
            throw new FileNotFoundException($"ffmpeg did not produce expected silent video: {silentVideoPath}");

        job.ExtractedAudioPath = audioPath;
        job.SilentVideoPath = silentVideoPath;
        job.State = JobState.AudioExtracted;
        await _repo.UpdateAsync(job, ct);

        return (audioPath, silentVideoPath);
    }
}
