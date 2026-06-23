using System.Text;
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
        IReadOnlyList<LanguageResult> languageResults,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.SilentVideoPath))
            throw new InvalidOperationException($"Job {job.Id} has no SilentVideoPath set.");
        if (string.IsNullOrEmpty(job.ExtractedAudioPath))
            throw new InvalidOperationException($"Job {job.Id} has no ExtractedAudioPath set.");
        if (languageResults.Count == 0)
            throw new InvalidOperationException($"Job {job.Id} has no language results to mux.");
        if (string.IsNullOrEmpty(job.SrtFilePath))
            throw new InvalidOperationException($"Job {job.Id} has no SrtFilePath set.");

        Directory.CreateDirectory(outputFolder);
        var outputPath = Path.Combine(outputFolder, job.OriginalFileName);

        var ext           = Path.GetExtension(job.OriginalFileName).ToLowerInvariant();
        var subtitleCodec = ext is ".mkv" ? "srt" : "mov_text";

        _logger.LogInformation(
            "Muxing {Video} + original audio + {N} language track(s) → {Out}",
            job.SilentVideoPath, languageResults.Count, outputPath);

        var args = BuildFfmpegArgs(job, languageResults, subtitleCodec, outputPath);

        await _runner.RunAsync(ffmpegPath, args, ct);

        if (!_fs.FileExists(outputPath))
            throw new FileNotFoundException($"ffmpeg did not produce output video at {outputPath}");

        job.OutputFilePath = outputPath;
        job.State          = JobState.AddedToOriginalVideo;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Output video written to {Path}", outputPath);
    }

    internal static string BuildFfmpegArgs(
        VideoJob job,
        IReadOnlyList<LanguageResult> languageResults,
        string subtitleCodec,
        string outputPath)
    {
        var sb = new StringBuilder();

        // ── Inputs ──────────────────────────────────────────────────────────────
        // [0] silent video
        sb.Append($"-y -i \"{job.SilentVideoPath}\" ");
        // [1] original audio
        sb.Append($"-i \"{job.ExtractedAudioPath}\" ");
        // [2..N+1] per-language mixed audio
        foreach (var lr in languageResults)
            sb.Append($"-i \"{lr.MixedAudioPath}\" ");
        // [N+2] original-language SRT (timing-matched to the original video)
        sb.Append($"-i \"{job.SrtFilePath}\" ");
        // [N+3..2N+2] per-language translated SRTs (timing adjusted to match TTS audio)
        foreach (var lr in languageResults)
            sb.Append($"-i \"{lr.TranslatedSrtFilePath}\" ");

        // ── filter_complex: apad on every audio stream ───────────────────────
        // apad pads with silence; -shortest stops at video EOF.
        var audioCount = 1 + languageResults.Count; // original + languages
        var fcParts    = Enumerable.Range(0, audioCount).Select(i => $"[{i + 1}:a]apad[a{i}]");
        sb.Append($"-filter_complex \"{string.Join(';', fcParts)}\" ");

        // ── Stream maps ──────────────────────────────────────────────────────
        sb.Append("-map 0:v:0 ");
        for (int i = 0; i < audioCount; i++)
            sb.Append($"-map [a{i}] ");
        var srtBase = 1 + audioCount; // first SRT input index (= original SRT)
        sb.Append($"-map {srtBase}:s:0 ");          // original-language SRT
        for (int i = 0; i < languageResults.Count; i++)
            sb.Append($"-map {srtBase + 1 + i}:s:0 "); // translated SRTs

        // ── Codecs ───────────────────────────────────────────────────────────
        sb.Append($"-c:v copy -c:a aac -b:a 192k -c:s {subtitleCodec} ");

        // ── Audio track metadata ─────────────────────────────────────────────
        sb.Append("-metadata:s:a:0 title=\"Original\" ");
        for (int i = 0; i < languageResults.Count; i++)
            sb.Append($"-metadata:s:a:{i + 1} title=\"{languageResults[i].Language}\" ");

        // ── Subtitle track metadata ──────────────────────────────────────────
        sb.Append("-metadata:s:s:0 title=\"Original\" ");
        for (int i = 0; i < languageResults.Count; i++)
            sb.Append($"-metadata:s:s:{i + 1} title=\"{languageResults[i].Language}\" ");

        sb.Append($"-shortest \"{outputPath}\"");

        return sb.ToString();
    }
}
