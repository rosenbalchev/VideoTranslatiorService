using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Data.Entities;
using RB.VideoTranslator.Data.Repositories;

namespace RB.VideoTranslator.BLL.Services;

public sealed class SrtTranslatorService : ISrtTranslatorService
{
    private const int ChunkSize = 50;

    private readonly IVideoJobRepository _repo;
    private readonly IFileSystem _fs;
    private readonly IAzureChatEngine _chat;
    private readonly ILogger<SrtTranslatorService> _logger;

    // Matches "[N] translated text" lines in GPT responses.
    private static readonly Regex MarkerRx = new(@"^\[(\d+)\]\s*(.*)", RegexOptions.Compiled);
    private static readonly Regex MarkupRx = new(@"<[^>]+>|\{\\[^}]*\}", RegexOptions.Compiled);

    public SrtTranslatorService(
        IVideoJobRepository repo,
        IFileSystem fs,
        IAzureChatEngine chat,
        ILogger<SrtTranslatorService> logger)
    {
        _repo   = repo;
        _fs     = fs;
        _chat   = chat;
        _logger = logger;
    }

    public async Task TranslateAsync(VideoJob job, string targetLanguage, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.SrtFilePath))
            throw new InvalidOperationException($"Job {job.Id} has no SrtFilePath set.");

        var content = await _fs.ReadAllTextAsync(job.SrtFilePath, ct);

        // Parse into structured blocks so timestamps are never sent to the model.
        var blocks = ParseBlocks(content);

        _logger.LogInformation(
            "Translating {Count} SRT blocks to {Lang} in chunks of {Chunk} — job {Id}",
            blocks.Count, targetLanguage, ChunkSize, job.Id);

        // Timestamps are owned entirely by this service and are never exposed to GPT.
        // GPT translates text only; we reconstruct the SRT with original timing afterwards.
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
            var lines      = new string[chunkCount];
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

        // Reconstruct SRT with ORIGINAL timestamps + translated text.
        var sb = new StringBuilder(blocks.Count * 80);
        for (int i = 0; i < blocks.Count; i++)
        {
            sb.AppendLine((i + 1).ToString());
            sb.AppendLine(blocks[i].TimestampLine);
            sb.AppendLine(translatedTexts[i] ?? blocks[i].Text); // fall back to source text if unparseable
            sb.AppendLine();
        }

        var baseName   = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var outputPath = Path.Combine(
            job.ProcessingFolderPath,
            $"{baseName}_translated_{targetLanguage}.srt");

        await _fs.WriteAllTextAsync(outputPath, sb.ToString(), ct);

        job.TranslatedSrtFilePath = outputPath;
        job.State                 = JobState.SrtTranslated;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Translated SRT written to {Path}", outputPath);
    }

    // Parses raw SRT content into structured blocks preserving the original timestamp line.
    // Strips inline markup from text (same as SrtToAzureTtsService) and joins multi-line
    // entries into a single string — the TTS step expects one text string per entry.
    internal static List<SrtBlock> ParseBlocks(string content)
    {
        var blocks    = new List<SrtBlock>();
        var rawBlocks = content
            .Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in rawBlocks)
        {
            var lines = raw.Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Length < 2) continue;

            var tsIdx = Array.FindIndex(lines, l => l.Contains("-->"));
            if (tsIdx < 0) continue;

            var textLines = lines
                .Skip(tsIdx + 1)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            if (textLines.Length == 0) continue;

            var text = MarkupRx.Replace(string.Join(" ", textLines), "").Trim();
            if (string.IsNullOrEmpty(text)) continue;

            blocks.Add(new SrtBlock(lines[tsIdx], text));
        }

        return blocks;
    }

    internal readonly record struct SrtBlock(string TimestampLine, string Text);
}
