using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class SrtToAzureTtsService : ISrtToAzureTtsService
{
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

        var baseName = Path.GetFileNameWithoutExtension(job.TranslatedSrtFilePath);

        var fullSsml = BuildSsml(entries, voiceName, lang);
        var ssmlPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}_azure_tts.ssml");
        await _fs.WriteAllTextAsync(ssmlPath, fullSsml, ct);

        _logger.LogInformation(
            "Synthesising Azure TTS audio ({Voice}/{Lang}) from {Srt} — {Count} entries. SSML: {Ssml}",
            voiceName, lang, job.TranslatedSrtFilePath, entries.Count, ssmlPath);

        var audioData = await _engine.SpeakSsmlAsync(fullSsml, endpointUrl, subscriptionKey, voiceName, ct);

        var outputWav = Path.Combine(job.ProcessingFolderPath, $"{baseName}_azure_tts.wav");
        await using var fileStream = _fs.Create(outputWav);
        await fileStream.WriteAsync(audioData, ct);

        job.AzureTtsAudioPath = outputWav;
        job.State             = JobState.AzureTtsSynthesised;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Azure TTS audio written to {Wav}", outputWav);
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

    // Fallback chars/sec used when the subtitle file provides no usable duration data.
    private const double FallbackCharsPerSecond = 13.0;

    // Default speaking rate applied to all normal entries — slightly slower than the
    // neural voice's natural pace to give the listener comfortable time to follow.
    // Only raised above this when text would genuinely overflow its subtitle window.
    public const double DefaultRatePct = 100.0;
    public const double MaxRatePct     = 100.0;

    internal static string BuildSsml(
        IReadOnlyList<SrtEntry> entries,
        string voiceName,
        string lang,
        int startOffsetMs = 0)
    {
        var sb = new StringBuilder(entries.Count * 160);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append($"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xmlns:mstts=\"http://www.w3.org/2001/mstts\" xml:lang=\"{lang}\">");
        sb.Append($"<voice name=\"{voiceName}\">");

        var curMs = startOffsetMs;
        foreach (var entry in entries)
        {
            var gap = Math.Max(0, entry.StartMs - curMs);
            if (gap > 0)
                sb.Append($"<break time=\"{gap}ms\"/>");

            var text        = XmlEscape(entry.Text);
            var availableMs = entry.EndMs - entry.StartMs;
            var rate        = SpeechRateFor(entry.Text, availableMs);

            if (Math.Abs(rate - 100.0) < 1.0)
                sb.Append(text);
            else
                sb.Append($"<prosody rate=\"{rate:F0}%\">{text}</prosody>");

            curMs = entry.EndMs;
        }

        sb.Append("</voice></speak>");
        return sb.ToString();
    }

    // Returns the prosody rate (%) for an entry.
    // charsPerSecond is derived from the subtitle file's own average pace so the estimate
    // is calibrated to the actual content rather than a generic constant.
    // - If text would overflow its window, speed up to fit (capped at MaxRatePct).
    // - Otherwise always use DefaultRatePct so the voice never rushes ahead of the subtitles.
    internal static double SpeechRateFor(string text, int availableMs,
        double charsPerSecond = FallbackCharsPerSecond)
    {
        if (availableMs <= 0 || text.Length == 0) return DefaultRatePct;
        var estimatedMs = text.Length / charsPerSecond * 1000.0;
        var naturalRate = estimatedMs / availableMs * 100.0;
        return naturalRate > DefaultRatePct
            ? Math.Min(naturalRate, MaxRatePct)
            : DefaultRatePct;
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
        const int sampleRate     = 44100;
        const int channels       = 1;
        const int bitsPerSample  = 16;
        const int bytesPerSample = bitsPerSample / 8;
        // Use long arithmetic — int × int overflows for gaps longer than ~48 seconds.
        var numSamples = (long)sampleRate * durationMs / 1000;
        var dataSize   = (int)(numSamples * channels * bytesPerSample);
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

    // Returns true if the byte array looks like a RIFF WAV with a findable "data" chunk.
    private static bool IsValidRiffWav(byte[] data)
    {
        if (data.Length < 12) return false;
        return data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E';
    }

    // Returns the duration of a valid WAV in milliseconds by reading the data chunk size.
    // Assumes 44100 Hz, 16-bit, mono (88200 bytes/sec) — matches MakeSilenceWav and Azure TTS output.
    private static int GetWavDurationMs(byte[] wav)
    {
        var dataOffset = FindPcmDataOffset(wav);
        var dataSize   = BitConverter.ToUInt32(wav, dataOffset - 4);
        return (int)(dataSize * 1000L / 88200);
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
