using System.Text;
using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Core.Services;

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

        _fs.CreateDirectory(outputFolder);

        var distinctLangResults = languageResults
            .GroupBy(l => l.Language)
            .Select(g => g.First())
            .ToList();

        var ext           = Path.GetExtension(job.OriginalFileName).ToLowerInvariant();
        var baseName      = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var outputExt     = ext is ".mkv" ? ".mkv" : ".mp4";
        var subtitleCodec = ext is ".mkv" ? "srt" : "mov_text";

        // ── Multi-audio master (all languages + original) ────────────────────
        var multiAudioPath = Path.Combine(outputFolder, $"{baseName}_multiAudio{outputExt}");

        _logger.LogInformation(
            "Muxing {Video} + original audio + {N} language track(s) → {Out}",
            job.SilentVideoPath, distinctLangResults.Count, multiAudioPath);

        var args = BuildFfmpegArgs(job, distinctLangResults, subtitleCodec, multiAudioPath);

        _logger.LogInformation("ffmpeg args: {Args}", args);
        await _runner.RunAsync(ffmpegPath, args, ct);

        if (!_fs.FileExists(multiAudioPath))
            throw new FileNotFoundException($"ffmpeg did not produce output video at {multiAudioPath}");

        job.OutputFilePath = multiAudioPath;
        job.State          = JobState.AddedToOriginalVideo;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Multi-audio video written to {Path}", multiAudioPath);

        // ── Per-language individual videos ───────────────────────────────────
        foreach (var lr in distinctLangResults)
        {
            ct.ThrowIfCancellationRequested();

            var langPath = Path.Combine(outputFolder, $"{baseName}_{lr.Language}{outputExt}");
            _logger.LogInformation("Generating {Language} video → {Out}", lr.Language, langPath);

            var langArgs = BuildSingleLanguageFfmpegArgs(job, lr, languageResults, subtitleCodec, langPath);
            _logger.LogInformation("ffmpeg args: {Args}", langArgs);
            await _runner.RunAsync(ffmpegPath, langArgs, ct);

            if (!_fs.FileExists(langPath))
                _logger.LogWarning("ffmpeg did not produce {Language} video at {Path}", lr.Language, langPath);
            else
                _logger.LogInformation("{Language} video written to {Path}", lr.Language, langPath);
        }
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

    internal static string BuildSingleLanguageFfmpegArgs(
        VideoJob job,
        LanguageResult lr,
        IReadOnlyList<LanguageResult> allLanguages,
        string subtitleCodec,
        string outputPath)
    {
        var sb = new StringBuilder();

        // Inputs: [0] silent video  [1] dubbed audio  [2] original SRT  [3..N+2] all translated SRTs
        sb.Append($"-y -i \"{job.SilentVideoPath}\" ");
        sb.Append($"-i \"{lr.MixedAudioPath}\" ");
        sb.Append($"-i \"{job.SrtFilePath}\" ");
        foreach (var lang in allLanguages)
            sb.Append($"-i \"{lang.TranslatedSrtFilePath}\" ");

        sb.Append("-filter_complex \"[1:a]apad[a0]\" ");

        // Video + audio
        sb.Append("-map 0:v:0 -map [a0] ");
        // Original subtitle then all language subtitles
        sb.Append("-map 2:s:0 ");
        for (int i = 0; i < allLanguages.Count; i++)
            sb.Append($"-map {3 + i}:s:0 ");

        sb.Append($"-c:v copy -c:a aac -b:a 192k -c:s {subtitleCodec} ");
        sb.Append($"-metadata:s:a:0 title=\"{lr.Language}\" ");
        sb.Append("-metadata:s:s:0 title=\"Original\" ");
        for (int i = 0; i < allLanguages.Count; i++)
            sb.Append($"-metadata:s:s:{i + 1} title=\"{allLanguages[i].Language}\" ");

        sb.Append($"-shortest \"{outputPath}\"");

        return sb.ToString();
    }
}
