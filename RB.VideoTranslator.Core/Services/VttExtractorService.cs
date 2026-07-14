using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Core.Services;

public sealed class VttExtractorService : IVttExtractorService
{
    // vtt_common.diarize_and_shape()/whisperx expect lowercase dict keys ("start"/"end"/"text")
    // — plain JsonSerializer.Serialize() would emit C#'s PascalCase property names instead,
    // which whisperx.assign_word_speakers silently mishandles (KeyError: 'end') rather than
    // rejecting outright.
    private static readonly JsonSerializerOptions SegmentsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly IWhisperOnnxTranscriberService _whisperOnnxTranscriber;
    private readonly ILogger<VttExtractorService> _logger;

    public VttExtractorService(
        IVideoJobRepository repo,
        IProcessRunner processRunner,
        IFileSystem fs,
        IWhisperOnnxTranscriberService whisperOnnxTranscriber,
        ILogger<VttExtractorService> logger)
    {
        _repo = repo;
        _processRunner = processRunner;
        _fs = fs;
        _whisperOnnxTranscriber = whisperOnnxTranscriber;
        _logger = logger;
    }

    public async Task ExtractAsync(
        VideoJob job,
        string pythonPath = "python",
        bool enableVoiceMarks = true,
        bool useOnnxTranscription = false,
        string? onnxModelPath = null,
        string ffmpegPath = "ffmpeg",
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.ExtractedAudioPath))
            throw new InvalidOperationException($"Job {job.Id} has no ExtractedAudioPath set.");

        var baseName = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var vttPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}.vtt");

        _logger.LogInformation(
            "Transcribing {Audio} → {Vtt} (this may take a while)",
            job.ExtractedAudioPath, vttPath);

        if (useOnnxTranscription)
        {
            if (string.IsNullOrEmpty(onnxModelPath))
                throw new InvalidOperationException(
                    "UseOnnxTranscription is enabled but no ONNX model path was resolved. " +
                    "Run scripts/export_onnx_models.bat or .sh first.");

            await ExtractWithOnnxAsync(job, pythonPath, enableVoiceMarks, onnxModelPath, ffmpegPath, vttPath, ct);
        }
        else
        {
            await ExtractWithWhisperXAsync(job, pythonPath, enableVoiceMarks, vttPath, ct);
        }

        if (!_fs.FileExists(vttPath))
            throw new FileNotFoundException($"Whisper did not produce expected VTT output: {vttPath}");

        job.VttFilePath = vttPath;
        job.State = JobState.VttExtracted;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("VTT written to {Vtt}", vttPath);
    }

    private async Task ExtractWithWhisperXAsync(
        VideoJob job, string pythonPath, bool enableVoiceMarks, string vttPath, CancellationToken ct)
    {
        var scriptPath = ResolveScriptPath("tool_wavToVttVoiceMark.py");
        var voiceMarksArg = enableVoiceMarks ? string.Empty : " --no-voice-marks";

        await _processRunner.RunAsync(
            pythonPath,
            $"\"{scriptPath}\" \"{job.ExtractedAudioPath}\" \"{vttPath}\"{voiceMarksArg}",
            ct);
    }

    private async Task ExtractWithOnnxAsync(
        VideoJob job, string pythonPath, bool enableVoiceMarks, string onnxModelPath, string ffmpegPath,
        string vttPath, CancellationToken ct)
    {
        var transcription = await _whisperOnnxTranscriber.TranscribeAsync(
            job.ExtractedAudioPath!, onnxModelPath, ffmpegPath, ct);

        if (!enableVoiceMarks)
        {
            await WriteUndiarizedVttAsync(vttPath, transcription.Segments, ct);
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var segmentsJsonPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}_segments.json");
        await _fs.WriteAllTextAsync(
            segmentsJsonPath, JsonSerializer.Serialize(transcription.Segments, SegmentsJsonOptions), ct);

        var scriptPath = ResolveScriptPath("tool_diarizeVtt.py");
        await _processRunner.RunAsync(
            pythonPath,
            $"\"{scriptPath}\" \"{job.ExtractedAudioPath}\" \"{segmentsJsonPath}\" \"{vttPath}\" " +
            $"--language {transcription.Language}",
            ct);
    }

    private static string ResolveScriptPath(string scriptName)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", scriptName);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Whisper tool not found: {scriptPath}");
        return scriptPath;
    }

    private async Task WriteUndiarizedVttAsync(
        string vttPath, IReadOnlyList<TranscribedSegment> segments, CancellationToken ct)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        for (int i = 0; i < segments.Count; i++)
        {
            sb.Append(i + 1).Append('\n');
            sb.Append(FormatTime(segments[i].Start)).Append(" --> ").Append(FormatTime(segments[i].End)).Append('\n');
            sb.Append(segments[i].Text).Append("\n\n");
        }
        await _fs.WriteAllTextAsync(vttPath, sb.ToString(), ct);
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}
