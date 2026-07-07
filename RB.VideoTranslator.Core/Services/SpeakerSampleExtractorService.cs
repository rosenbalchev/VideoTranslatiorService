using System.Text.Json;
using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class SpeakerSampleExtractorService : ISpeakerSampleExtractorService
{
    private const string SpeakerSamplesFolderName = "speaker_samples";

    // Mirrors MIN_SAMPLE_DURATION_SECONDS in tool_wavToVtt_*.py — must stay identical so the
    // text picked here always matches the segment the header/audio extraction used.
    private static readonly TimeSpan MinSampleDuration = TimeSpan.FromSeconds(4);

    private readonly IVideoJobRepository _repo;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fs;
    private readonly ILogger<SpeakerSampleExtractorService> _logger;

    public SpeakerSampleExtractorService(
        IVideoJobRepository repo,
        IProcessRunner processRunner,
        IFileSystem fs,
        ILogger<SpeakerSampleExtractorService> logger)
    {
        _repo = repo;
        _processRunner = processRunner;
        _fs = fs;
        _logger = logger;
    }

    public async Task ExtractAsync(VideoJob job, string ffmpegPath = "ffmpeg", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.VocalsAudioPath))
            throw new InvalidOperationException($"Job {job.Id} has no VocalsAudioPath set.");
        if (string.IsNullOrEmpty(job.VttFilePath))
            throw new InvalidOperationException($"Job {job.Id} has no VttFilePath set.");

        // The ORIGINAL (source-language) VTT carries the speaker/gender summary header —
        // translated VTTs merely preserve it verbatim, so the original is authoritative.
        var vttContent = await _fs.ReadAllTextAsync(job.VttFilePath, ct);
        var speakers = ParseSpeakerRows(VttTranslatorService.ExtractLeadingNote(vttContent));
        var cuesBySpeaker = ParseCuesBySpeaker(vttContent);

        if (speakers.Count == 0)
        {
            _logger.LogInformation(
                "Job {Id}: no speaker summary found in {Vtt} — skipping speaker sample extraction",
                job.Id, job.VttFilePath);
            job.State = JobState.SpeakerSamplesExtracted;
            await _repo.UpdateAsync(job, ct);
            return;
        }

        var samplesDir = Path.Combine(job.ProcessingFolderPath, SpeakerSamplesFolderName);
        _fs.CreateDirectory(samplesDir);

        var samplePaths = new Dictionary<string, string>();
        foreach (var speaker in speakers)
        {
            ct.ThrowIfCancellationRequested();

            var outPath = Path.Combine(samplesDir, $"{speaker.Label}.wav");

            _logger.LogInformation(
                "Extracting speaker sample {Label} ({Gender}) [{Start}→{End}] → {Out}",
                speaker.Label, speaker.Gender, speaker.Start, speaker.End, outPath);

            await _processRunner.RunAsync(
                ffmpegPath,
                $"-y -i \"{job.VocalsAudioPath}\" -ss {Format(speaker.Start)} -to {Format(speaker.End)} " +
                $"-c:a pcm_s16le \"{outPath}\"",
                ct);

            if (!_fs.FileExists(outPath))
                throw new FileNotFoundException($"ffmpeg did not produce expected speaker sample: {outPath}");

            samplePaths[speaker.Label] = outPath;

            // Companion transcript: what the speaker said during the SAME cue the .wav was
            // extracted from. Re-derived independently from the full transcript (not the
            // header's whole-second-truncated times) using the identical selection rule as
            // tool_wavToVtt_*.py's find_speaker_sample_segment — first cue over
            // MinSampleDuration, falling back to the longest — so text and audio never
            // refer to different segments.
            var text = cuesBySpeaker.TryGetValue(speaker.Label, out var cues)
                ? FindSampleCue(cues)?.Text ?? string.Empty
                : string.Empty;

            var txtPath = Path.Combine(samplesDir, $"{speaker.Label}.txt");
            await _fs.WriteAllTextAsync(txtPath, text, ct);
        }

        job.SpeakerSamplePathsJson = JsonSerializer.Serialize(samplePaths);
        job.State = JobState.SpeakerSamplesExtracted;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Extracted {Count} speaker sample(s) to {Dir}", samplePaths.Count, samplesDir);
    }

    private static string Format(TimeSpan t) => t.ToString(@"hh\:mm\:ss");

    // Parses the pipe-delimited speaker rows out of the leading NOTE block written by
    // tool_wavToVtt_*.py, e.g. "Speaker1|Female|00:00:00|00:00:26|232Hz". Lines that aren't
    // valid data rows (the "NOTE" marker itself, the summary sentence) are silently skipped.
    internal static List<SpeakerRow> ParseSpeakerRows(string? leadingNote)
    {
        var rows = new List<SpeakerRow>();
        if (string.IsNullOrEmpty(leadingNote)) return rows;

        foreach (var line in leadingNote.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 4) continue;
            if (!TimeSpan.TryParse(parts[2], out var start)) continue;
            if (!TimeSpan.TryParse(parts[3], out var end)) continue;

            rows.Add(new SpeakerRow(parts[0], parts[1], start, end));
        }

        return rows;
    }

    internal readonly record struct SpeakerRow(string Label, string Gender, TimeSpan Start, TimeSpan End);

    // Same selection rule as find_speaker_sample_segment() in tool_wavToVtt_*.py: the first
    // cue (in chronological order) longer than MinSampleDuration, falling back to the longest
    // cue overall if none qualify. `cues` must be in file order, as returned by ParseCuesBySpeaker.
    internal static SpeakerCue? FindSampleCue(List<SpeakerCue> cues)
    {
        foreach (var cue in cues)
        {
            if (cue.End - cue.Start > MinSampleDuration)
                return cue;
        }
        return cues.Count > 0 ? cues.MaxBy(c => c.End - c.Start) : null;
    }

    // Parses the per-cue "NOTE {Label} (estimated: {gender})" comments that tool_wavToVtt_*.py
    // writes immediately before each diarized cue, grouping every cue's timing + text by
    // speaker label, in file order. Used to recover the actual spoken text for the segment
    // FindSampleCue selects — the header only carries whole-second-truncated times.
    internal static Dictionary<string, List<SpeakerCue>> ParseCuesBySpeaker(string content)
    {
        var result = new Dictionary<string, List<SpeakerCue>>();
        var rawBlocks = content
            .Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);

        string? currentLabel = null;
        foreach (var raw in rawBlocks)
        {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("NOTE", StringComparison.Ordinal))
            {
                // Per-cue label note is a single line: "NOTE Speaker1 (estimated: female)".
                // Any other NOTE block (e.g. the multi-line summary header) is not a label.
                var firstNewline = trimmed.IndexOf('\n');
                var afterNote = (firstNewline < 0 ? trimmed["NOTE".Length..] : null)?.Trim();
                var spaceIdx = afterNote?.IndexOf(' ') ?? -1;
                currentLabel = spaceIdx > 0 ? afterNote![..spaceIdx] : null;
                continue;
            }

            if (currentLabel is null) continue;

            var lines = trimmed.Split('\n');
            var tsIdx = Array.FindIndex(lines, l => l.Contains("-->"));
            if (tsIdx < 0) { currentLabel = null; continue; }

            var tsParts = lines[tsIdx].Split("-->", StringSplitOptions.TrimEntries);
            if (tsParts.Length != 2 ||
                !TimeSpan.TryParse(tsParts[0], out var start) ||
                !TimeSpan.TryParse(tsParts[1], out var end))
            {
                currentLabel = null;
                continue;
            }

            var text = string.Join(" ", lines.Skip(tsIdx + 1)).Trim();

            if (!result.TryGetValue(currentLabel, out var list))
                result[currentLabel] = list = [];
            list.Add(new SpeakerCue(start, end, text));

            currentLabel = null; // each label note pairs with exactly one following cue
        }

        return result;
    }

    internal readonly record struct SpeakerCue(TimeSpan Start, TimeSpan End, string Text);
}
