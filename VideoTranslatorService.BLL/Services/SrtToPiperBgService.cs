using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class SrtToAzureTtsService : ISrtToAzureTtsService
{
    // Azure Speech SDK cancels a request if it receives no audio frame for 3 000 ms.
    // The server generates silence for <break> elements as a single delayed frame, so a
    // break longer than this threshold triggers the watchdog. We handle larger gaps by
    // inserting raw silence WAV ourselves and never put them in the SSML.
    private const int MaxSilenceBreakMs = 2500;

    // Hard cap on entries per synthesis call as a secondary safety measure.
    private const int MaxEntriesPerSegment = 30;

    private readonly IVideoJobRepository _repo;
    private readonly IFileSystem _fs;
    private readonly IAzureSpeechEngine _engine;
    private readonly ILogger<SrtToAzureTtsService> _logger;

    private static readonly Regex TimeLineRx = new(
        @"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})",
        RegexOptions.Compiled);

    private static readonly Regex MarkupRx = new(
        @"<[^>]+>|\{\\[^}]*\}",
        RegexOptions.Compiled);

    public SrtToAzureTtsService(
        IVideoJobRepository repo,
        IFileSystem fs,
        IAzureSpeechEngine engine,
        ILogger<SrtToAzureTtsService> logger)
    {
        _repo   = repo;
        _fs     = fs;
        _engine = engine;
        _logger = logger;
    }

    public async Task SynthesiseAsync(
        VideoJob job,
        string subscriptionKey,
        string endpointUrl,
        string voiceName = "en-US-Ava:DragonHDLatestNeural",
        string lang = "en-US",
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.TranslatedSrtFilePath))
            throw new InvalidOperationException($"Job {job.Id} has no TranslatedSrtFilePath set.");

        var srtContent = await _fs.ReadAllTextAsync(job.TranslatedSrtFilePath, ct);
        var entries    = ParseSrt(srtContent);

        if (entries.Count == 0)
            throw new InvalidOperationException($"No subtitle entries found in {job.SrtFilePath}.");

        var baseName = Path.GetFileNameWithoutExtension(job.OriginalFileName);

        // Write full SSML to disk for inspection (includes all breaks, even large ones)
        var fullSsml = BuildSsml(entries, voiceName, lang);
        var ssmlPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}_azure_tts.ssml");
        await _fs.WriteAllTextAsync(ssmlPath, fullSsml, ct);

        _logger.LogInformation(
            "Synthesising Azure TTS audio ({Voice}/{Lang}) from {Srt} — {Count} entries. SSML: {Ssml}",
            voiceName, lang, job.SrtFilePath, entries.Count, ssmlPath);

        // Split into segments so no SSML break exceeds MaxSilenceBreakMs.
        // Large inter-segment gaps are filled with raw silence WAV instead.
        var audioData = await SynthesiseInSegmentsAsync(entries, endpointUrl, subscriptionKey, voiceName, lang, ct);

        var outputWav = Path.Combine(job.ProcessingFolderPath, $"{baseName}_azure_tts.wav");
        await using var fileStream = _fs.Create(outputWav);
        await fileStream.WriteAsync(audioData, ct);

        job.AzureTtsAudioPath = outputWav;
        job.State             = JobState.AzureTtsSynthesised;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Azure TTS audio written to {Wav}", outputWav);
    }

    // ── Segmented synthesis ───────────────────────────────────────────────────

    private async Task<byte[]> SynthesiseInSegmentsAsync(
        IReadOnlyList<SrtEntry> entries,
        string endpointUrl,
        string subscriptionKey,
        string voiceName,
        string lang,
        CancellationToken ct)
    {
        var segments = SplitIntoSegments(entries, MaxSilenceBreakMs, MaxEntriesPerSegment);
        _logger.LogInformation("Synthesis split into {Count} segment(s)", segments.Count);

        var chunks = new List<byte[]>(segments.Count * 2);
        var curMs  = 0;

        for (int i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var segment   = segments[i];
            var silenceMs = Math.Max(0, segment[0].StartMs - curMs);

            if (silenceMs > 0)
                chunks.Add(MakeSilenceWav(silenceMs));

            // startOffsetMs = segment start so BuildSsml produces zero leading break;
            // all internal breaks are < MaxSilenceBreakMs by construction.
            var ssml = BuildSsml(segment, voiceName, lang, startOffsetMs: segment[0].StartMs);

            _logger.LogInformation(
                "Synthesising segment {Seg}/{Total} ({Count} entries, leading silence {Ms} ms)",
                i + 1, segments.Count, segment.Count, silenceMs);

            chunks.Add(await _engine.SpeakSsmlAsync(ssml, endpointUrl, subscriptionKey, voiceName, ct));
            curMs = segment[^1].EndMs;
        }

        return ConcatenateWav(chunks);
    }

    // Groups consecutive entries into segments such that no gap between neighbours
    // within a segment exceeds maxGapMs and no segment has more than maxEntries entries.
    internal static List<List<SrtEntry>> SplitIntoSegments(
        IReadOnlyList<SrtEntry> entries,
        int maxGapMs,
        int maxEntries = int.MaxValue)
    {
        var segments = new List<List<SrtEntry>>();
        if (entries.Count == 0) return segments;

        var current = new List<SrtEntry> { entries[0] };

        for (int i = 1; i < entries.Count; i++)
        {
            var gap = entries[i].StartMs - entries[i - 1].EndMs;
            if (gap > maxGapMs || current.Count >= maxEntries)
            {
                segments.Add(current);
                current = new List<SrtEntry>();
            }
            current.Add(entries[i]);
        }

        segments.Add(current);
        return segments;
    }

    // ── SSML builder ─────────────────────────────────────────────────────────

    internal static string BuildSsml(
        IReadOnlyList<SrtEntry> entries,
        string voiceName,
        string lang,
        int startOffsetMs = 0)
    {
        var sb = new StringBuilder(entries.Count * 120);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append($"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xmlns:mstts=\"http://www.w3.org/2001/mstts\" xml:lang=\"{lang}\">");
        sb.Append($"<voice name=\"{voiceName}\">");

        var curMs = startOffsetMs;
        foreach (var entry in entries)
        {
            var gap = Math.Max(0, entry.StartMs - curMs);
            if (gap > 0)
                sb.Append($"<break time=\"{gap}ms\"/>");
            sb.Append(XmlEscape(entry.Text));
            curMs = entry.EndMs;
        }

        sb.Append("</voice></speak>");
        return sb.ToString();
    }

    // ── SRT parser ────────────────────────────────────────────────────────────

    internal static List<SrtEntry> ParseSrt(string content)
    {
        var entries = new List<SrtEntry>();
        var blocks  = Regex.Split(content.Replace("\r\n", "\n").Trim(), @"\n\s*\n");

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) continue;

            var timeLine = Array.Find(lines, l => l.Contains("-->"));
            if (timeLine is null) continue;

            var m = TimeLineRx.Match(timeLine);
            if (!m.Success) continue;

            var startMs = ToMs(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            var endMs   = ToMs(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);

            var timeIdx = Array.IndexOf(lines, timeLine);
            var raw  = string.Join(" ", lines.Skip(timeIdx + 1));
            var text = MarkupRx.Replace(raw, "");
            text = text.Replace("&nbsp;", " ").Replace("&amp;", "&");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (!string.IsNullOrEmpty(text))
                entries.Add(new SrtEntry(startMs, endMs, text));
        }

        return entries;
    }

    // ── WAV utilities ─────────────────────────────────────────────────────────

    // Generates a silent PCM WAV matching the Azure TTS output format (44100 Hz, 16-bit, mono).
    internal static byte[] MakeSilenceWav(int durationMs)
    {
        const int sampleRate    = 44100;
        const int channels      = 1;
        const int bitsPerSample = 16;
        const int bytesPerSample = bitsPerSample / 8;
        var numSamples = (int)Math.Round(sampleRate * durationMs / 1000.0);
        var dataSize   = numSamples * channels * bytesPerSample;
        var wav        = new byte[44 + dataSize];

        "RIFF"u8.CopyTo(wav);
        BitConverter.GetBytes((uint)(36 + dataSize)).CopyTo(wav, 4);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BitConverter.GetBytes(16u).CopyTo(wav, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(wav, 20);                         // PCM
        BitConverter.GetBytes((ushort)channels).CopyTo(wav, 22);
        BitConverter.GetBytes((uint)sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes((uint)(sampleRate * channels * bytesPerSample)).CopyTo(wav, 28);
        BitConverter.GetBytes((ushort)(channels * bytesPerSample)).CopyTo(wav, 32);
        BitConverter.GetBytes((ushort)bitsPerSample).CopyTo(wav, 34);
        "data"u8.CopyTo(wav.AsSpan(36));
        BitConverter.GetBytes((uint)dataSize).CopyTo(wav, 40);
        // data bytes are already 0 (silence)
        return wav;
    }

    // Finds the byte offset where PCM data begins (past the "data" chunk header).
    private static int FindPcmDataOffset(byte[] wav)
    {
        for (int i = 12; i < wav.Length - 8; i++)
        {
            if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a')
                return i + 8; // skip "data" (4) + chunk size (4)
        }
        throw new InvalidOperationException("WAV 'data' chunk not found in Azure TTS output.");
    }

    internal static byte[] ConcatenateWav(IReadOnlyList<byte[]> chunks)
    {
        if (chunks.Count == 1) return chunks[0];

        var dataOffsets = chunks.Select(FindPcmDataOffset).ToArray();
        var totalPcm    = chunks.Select((c, i) => c.Length - dataOffsets[i]).Sum();
        var headerLen   = dataOffsets[0];

        using var ms = new MemoryStream(headerLen + totalPcm);

        ms.Write(chunks[0], 0, headerLen);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            ms.Position = 4;              // RIFF chunk size
            bw.Write((uint)(headerLen + totalPcm - 8));
            ms.Position = headerLen - 4;  // "data" chunk size
            bw.Write((uint)totalPcm);
        }

        ms.Position = headerLen;
        foreach (var (chunk, dataOffset) in chunks.Zip(dataOffsets))
            ms.Write(chunk, dataOffset, chunk.Length - dataOffset);

        return ms.ToArray();
    }

    private static int ToMs(string h, string m, string s, string ms) =>
        int.Parse(h) * 3_600_000 + int.Parse(m) * 60_000 + int.Parse(s) * 1_000 + int.Parse(ms);

    private static string XmlEscape(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}

internal sealed record SrtEntry(int StartMs, int EndMs, string Text);
