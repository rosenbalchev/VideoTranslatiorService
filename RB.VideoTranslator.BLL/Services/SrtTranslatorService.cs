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

        // Split at blank-line boundaries, preserving raw block text exactly.
        var blocks = content
            .Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);

        _logger.LogInformation(
            "Translating {Count} SRT blocks to {Lang} in chunks of {Chunk} — job {Id}",
            blocks.Length, targetLanguage, ChunkSize, job.Id);

        var systemPrompt =
            $"You are a subtitle translator. Translate ONLY the dialogue text lines to {targetLanguage}. " +
            "Keep all index numbers and timestamp lines (HH:MM:SS,mmm --> HH:MM:SS,mmm) exactly unchanged. " +
            "Preserve blank lines between entries. Output only the translated SRT blocks with no extra commentary.";

        var translatedParts = new List<string>(blocks.Length / ChunkSize + 1);

        for (int i = 0; i < blocks.Length; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = blocks.Skip(i).Take(ChunkSize);
            var userContent = string.Join("\n\n", chunk);

            _logger.LogInformation(
                "Sending chunk {From}-{To}/{Total} to GPT...",
                i + 1, Math.Min(i + ChunkSize, blocks.Length), blocks.Length);

            var translated = await _chat.CompleteChatAsync(systemPrompt, userContent, ct);
            translatedParts.Add(translated.Trim());
        }

        var translatedSrt = string.Join("\n\n", translatedParts);

        var baseName   = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var outputPath = Path.Combine(
            job.ProcessingFolderPath,
            $"{baseName}_translated_{targetLanguage}.srt");

        await _fs.WriteAllTextAsync(outputPath, translatedSrt, ct);

        job.TranslatedSrtFilePath = outputPath;
        job.State                 = JobState.SrtTranslated;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Translated SRT written to {Path}", outputPath);
    }
}
