using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class VttTranslatorService : IVttTranslatorService
{
    private const int ChunkSize = 50;

    private readonly IVideoJobRepository _repo;
    private readonly IFileSystem _fs;
    private readonly IAzureChatEngine _chat;
    private readonly ILogger<VttTranslatorService> _logger;

    // Matches "[N] translated text" lines in GPT responses.
    private static readonly Regex MarkerRx = new(@"^\[(\d+)\]\s*(.*)", RegexOptions.Compiled);
    private static readonly Regex MarkupRx = new(@"<[^>]+>|\{\\[^}]*\}", RegexOptions.Compiled);

    public VttTranslatorService(
        IVideoJobRepository repo,
        IFileSystem fs,
        IAzureChatEngine chat,
        ILogger<VttTranslatorService> logger)
    {
        _repo = repo;
        _fs = fs;
        _chat = chat;
        _logger = logger;
    }

    public async Task TranslateAsync(VideoJob job, string targetLanguage, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.VttFilePath))
            throw new InvalidOperationException($"Job {job.Id} has no VttFilePath set.");

        var content = await _fs.ReadAllTextAsync(job.VttFilePath, ct);

        // The speaker/gender summary NOTE block (if present) is metadata, not dialogue —
        // never send it to GPT, but keep it verbatim in the translated output.
        var leadingNote = ExtractLeadingNote(content);

        // Parse into structured blocks so timestamps (and per-cue speaker NOTE lines) are
        // never sent to the model.
        var blocks = ParseBlocks(content);

        _logger.LogInformation(
            "Translating {Count} VTT blocks to {Lang} in chunks of {Chunk} — job {Id}",
            blocks.Count, targetLanguage, ChunkSize, job.Id);

        // Timestamps are owned entirely by this service and are never exposed to GPT.
        // GPT translates text only; we reconstruct the VTT with original timing afterwards.
        var systemPrompt =
            $"You are a subtitle translator. Translate subtitle text to {targetLanguage}. " +
            "Each input line has the format [N] text where N is a number. " +
            "Output exactly one line per input line, keeping the [N] prefix unchanged, " +
            "followed by the translated text. Do not add, remove, or renumber entries. " +
            "Output only the translated lines with no extra commentary.";

        var translatedTexts = new string?[blocks.Count];

        for (int i = 0; i < blocks.Count; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunkCount = Math.Min(ChunkSize, blocks.Count - i);
            var lines = new string[chunkCount];
            for (int j = 0; j < chunkCount; j++)
                lines[j] = $"[{i + j + 1}] {blocks[i + j].Text}";

            var userContent = string.Join("\n", lines);

            _logger.LogInformation(
                "Sending chunk {From}-{To}/{Total} to GPT...",
                i + 1, Math.Min(i + ChunkSize, blocks.Count), blocks.Count);

            var response = await _chat.CompleteChatAsync(systemPrompt, userContent, ct);

            foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var m = MarkerRx.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n <= blocks.Count)
                    translatedTexts[n - 1] = m.Groups[2].Value.Trim();
            }
        }

        // Reconstruct VTT with ORIGINAL timestamps + translated text.
        var sb = new StringBuilder(blocks.Count * 80 + 8);
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        if (leadingNote is not null)
        {
            sb.AppendLine(leadingNote);
            sb.AppendLine();
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].NoteLine is not null)
            {
                sb.AppendLine(blocks[i].NoteLine);
                sb.AppendLine();
            }
            sb.AppendLine((i + 1).ToString());
            sb.AppendLine(blocks[i].TimestampLine);
            sb.AppendLine(translatedTexts[i] ?? blocks[i].Text); // fall back to source text if unparseable
            sb.AppendLine();
        }

        var baseName = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var outputPath = Path.Combine(
            job.ProcessingFolderPath,
            $"{baseName}_translated_{targetLanguage}.vtt");

        await _fs.WriteAllTextAsync(outputPath, sb.ToString(), ct);

        job.TranslatedVttFilePath = outputPath;
        job.State                 = JobState.VttTranslated;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Translated VTT written to {Path}", outputPath);
    }

    // Returns the raw text of the speaker/gender summary NOTE block that
    // tool_wavToVtt_*.py writes immediately after the WEBVTT header, if present.
    // Only the block in that specific position is treated as the summary — later NOTE
    // blocks (the per-cue speaker labels) are intentionally left out of this check and
    // remain dropped from the translated output, unchanged from prior behaviour.
    internal static string? ExtractLeadingNote(string content)
    {
        var rawBlocks = content
            .Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);

        if (rawBlocks.Length < 2) return null;

        var candidate = rawBlocks[1].Trim();
        return candidate.StartsWith("NOTE", StringComparison.Ordinal) && !candidate.Contains("-->")
            ? candidate
            : null;
    }

    // Parses raw VTT content into structured blocks preserving the original timestamp line
    // and, if one immediately precedes the cue, its per-cue speaker NOTE line verbatim.
    // Strips inline markup from text (same as VttToAzureTtsService) and joins multi-line
    // entries into a single string — the TTS step expects one text string per entry.
    // The leading "WEBVTT" header naturally falls out: it splits into its own single-line
    // block, which is skipped by the `lines.Length < 2` guard below.
    internal static List<VttBlock> ParseBlocks(string content)
    {
        var blocks    = new List<VttBlock>();
        var rawBlocks = content
            .Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);

        // Must run BEFORE the `lines.Length < 2` guard below — a per-cue NOTE block is a
        // single physical line, so it would otherwise be silently dropped by that guard
        // before ever reaching the NOTE check (mirrors SpeakerSampleExtractorService.
        // ParseCuesBySpeaker, which gets this ordering right).
        string? pendingNote = null;
        foreach (var raw in rawBlocks)
        {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("NOTE", StringComparison.Ordinal))
            {
                // Only a single-line note (e.g. "NOTE Speaker1 (estimated: female)") is a
                // per-cue speaker label; the multi-line summary header is handled
                // separately by ExtractLeadingNote and must not be treated as one.
                pendingNote = trimmed.Contains('\n') ? null : trimmed;
                continue;
            }

            var lines = raw.Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Length < 2) { pendingNote = null; continue; }

            var tsIdx = Array.FindIndex(lines, l => l.Contains("-->"));
            if (tsIdx < 0) { pendingNote = null; continue; }

            var textLines = lines
                .Skip(tsIdx + 1)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            if (textLines.Length == 0) { pendingNote = null; continue; }

            var text = MarkupRx.Replace(string.Join(" ", textLines), "").Trim();
            if (string.IsNullOrEmpty(text)) { pendingNote = null; continue; }

            blocks.Add(new VttBlock(lines[tsIdx], text, pendingNote));
            pendingNote = null; // each label note pairs with exactly one following cue
        }

        return blocks;
    }

    internal readonly record struct VttBlock(string TimestampLine, string Text, string? NoteLine = null);
}
