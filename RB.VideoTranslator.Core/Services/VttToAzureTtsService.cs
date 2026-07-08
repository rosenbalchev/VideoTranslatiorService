using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Core.Services;

public sealed class VttToAzureTtsService : IVttToAzureTtsService
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
    private readonly ILogger<VttToAzureTtsService> _logger;

    private static readonly Regex TimeLineRx = new(
        @"(\d{2}):(\d{2}):(\d{2})\.(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})\.(\d{3})",
        RegexOptions.Compiled);

    private static readonly Regex MarkupRx = new(
        @"<[^>]+>|\{\\[^}]*\}",
        RegexOptions.Compiled);

    public VttToAzureTtsService(
        IVideoJobRepository repo,
        IFileSystem fs,
        IAzureSpeechEngine engine,
        ILogger<VttToAzureTtsService> logger)
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
        IReadOnlyDictionary<string, string>? speakerVoices = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.TranslatedVttFilePath))
            throw new InvalidOperationException($"Job {job.Id} has no TranslatedVttFilePath set.");

        var vttContent = await _fs.ReadAllTextAsync(job.TranslatedVttFilePath, ct);
        var entries    = ParseVtt(vttContent);

        if (entries.Count == 0)
            throw new InvalidOperationException($"No subtitle entries found in {job.VttFilePath}.");

        // Use the translated VTT filename as base so each language gets its own output files
        // (e.g. "video_translated_Bulgarian_azure_tts.wav" vs "video_translated_German_azure_tts.wav").
        var baseName = Path.GetFileNameWithoutExtension(job.TranslatedVttFilePath);

        // Write full SSML to disk for inspection (includes all breaks, even large ones).
        // Deliberately single-voice even when speakerVoices is set — this file is a debug
        // artifact only, never used for actual synthesis (see SynthesisePerEntryAsync).
        var fullSsml = BuildSsml(entries, voiceName, lang);
        var ssmlPath = Path.Combine(job.ProcessingFolderPath, $"{baseName}_azure_tts.ssml");
        await _fs.WriteAllTextAsync(ssmlPath, fullSsml, ct);

        // Compute chars/sec from this file's own subtitle timing rather than a hardcoded guess.
        var totalChars = entries.Sum(e => e.Text.Length);
        var totalMs    = entries.Sum(e => e.EndMs - e.StartMs);
        var charsPerSecond = totalMs > 0
            ? totalChars / (totalMs / 1000.0)
            : FallbackCharsPerSecond;

        _logger.LogInformation(
            "Synthesising Azure TTS audio ({Voice}/{Lang}) from {Vtt} — {Count} entries. " +
            "Subtitle avg pace: {Cps:F1} chars/sec. SSML: {Ssml}",
            voiceName, lang, job.TranslatedVttFilePath, entries.Count, charsPerSecond, ssmlPath);

        // One TTS call per subtitle entry. Leading silence per entry is derived from the
        // absolute original timestamp so the audio track aligns with the source video.
        // The translated VTT is NOT rewritten — subtitle display times are kept identical
        // to the original so all language tracks share the same visual timing.
        var audioData = await SynthesisePerEntryAsync(entries, endpointUrl, subscriptionKey, voiceName, lang, charsPerSecond, job.AudioChannels, speakerVoices, ct);

        var outputWav = Path.Combine(job.ProcessingFolderPath, $"{baseName}_azure_tts.wav");
        await using var fileStream = _fs.Create(outputWav);
        await fileStream.WriteAsync(audioData, ct);

        job.AzureTtsAudioPath = outputWav;
        job.State             = JobState.AzureTtsSynthesised;
        await _repo.UpdateAsync(job, ct);

        _logger.LogInformation("Azure TTS audio written to {Wav}", outputWav);
    }

    // ── Per-entry synthesis ───────────────────────────────────────────────────
    // One TTS call per subtitle entry. Leading silence is computed from the absolute
    // original timestamp so the audio track aligns with the source video.
    // When TTS audio overruns its window, the excess is logged and curMs advances
    // accordingly; the next entry's leading silence absorbs the difference.

    private const int MaxTtsRetries = 3;

    // Extra attempts made when synthesised audio overruns its subtitle window. Each retry
    // recalculates the prosody rate from the actual overrun ratio (measured audio duration,
    // not the pre-synthesis chars/sec estimate), so it converges on the real window fit.
    private const int MaxOverrunRetries = 2;

    // Retries are allowed to push the rate past MaxRatePct — that cap governs the
    // pre-synthesis estimate, whereas here we're correcting a measured overrun.
    private const double MaxOverrunRetryRatePct = 200.0;

    // Overrun below this is not worth a re-synthesis round-trip.
    private const int OverrunToleranceMs = 100;

    private async Task<byte[]> SynthesisePerEntryAsync(
        IReadOnlyList<VttEntry> entries,
        string endpointUrl,
        string subscriptionKey,
        string voiceName,
        string lang,
        double charsPerSecond,
        int inputAudioChannels,
        IReadOnlyDictionary<string, string>? speakerVoices,
        CancellationToken ct)
    {
        _logger.LogInformation("Synthesising {Count} entries one-by-one for precise sync", entries.Count);

        // Phase 1: TTS calls — collect all results and detect the actual output format
        // from the first valid chunk so silence chunks always match.
        var rawResults     = new SpeechAudioResult?[entries.Count];
        var sampleRate     = 44100;
        var channels       = 1;
        var bitsPerSample  = 16;
        var formatDetected = false;

        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var entry      = entries[i];
            var expectedMs = entry.EndMs - entry.StartMs;
            var rate       = SpeechRateFor(entry.Text, expectedMs, charsPerSecond);

            // Use this entry's assigned per-speaker voice when one exists, otherwise the
            // single default voice (also the behaviour when diarization produced no header).
            var entryVoice = entry.Speaker is not null
                && speakerVoices is not null
                && speakerVoices.TryGetValue(entry.Speaker, out var mappedVoice)
                    ? mappedVoice
                    : voiceName;

            var ssml = BuildEntrySsml(entry.Text, entryVoice, lang, rate);

            _logger.LogInformation(
                "TTS entry {I}/{Total} [{Start}→{End}ms] voice={Voice} rate={Rate:F0}%",
                i + 1, entries.Count, entry.StartMs, entry.EndMs, entryVoice, rate);

            // Retry on transient Azure SDK timeouts (frame-interval watchdog fires ~3 000ms).
            SpeechAudioResult? result = null;
            for (int attempt = 1; attempt <= MaxTtsRetries; attempt++)
            {
                try
                {
                    result = await _engine.SpeakSsmlAsync(ssml, endpointUrl, subscriptionKey, entryVoice, ct);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (attempt == MaxTtsRetries)
                    {
                        _logger.LogWarning(
                            "Entry {I}/{Total} failed all {Max} TTS attempts — substituting silence. Last: {Msg}",
                            i + 1, entries.Count, MaxTtsRetries, ex.Message);
                    }
                    else
                    {
                        var delayMs = 500 * attempt;
                        _logger.LogWarning(
                            "Entry {I}/{Total} TTS attempt {Attempt}/{Max} failed ({Msg}) — retrying in {Delay}ms",
                            i + 1, entries.Count, attempt, MaxTtsRetries, ex.Message, delayMs);
                        await Task.Delay(delayMs, ct);
                    }
                }
            }

            // If the synthesised audio overruns its subtitle window, recalculate the prosody
            // rate from the actual overrun ratio and re-synthesise so the audio matches the
            // window. An underrun is left alone — Phase 2 pads it with trailing silence.
            if (result is not null && expectedMs > 0)
            {
                for (int overrunAttempt = 1; overrunAttempt <= MaxOverrunRetries; overrunAttempt++)
                {
                    var actualMs = result.DurationMs;
                    if (actualMs <= expectedMs + OverrunToleranceMs)
                        break;

                    var newRate = Math.Min(rate * actualMs / expectedMs, MaxOverrunRetryRatePct);
                    if (newRate <= rate)
                        break; // already at the retry ceiling — retrying again won't help

                    _logger.LogInformation(
                        "Entry {I}/{Total} overran its window ({Actual}ms > {Expected}ms) at rate={Rate:F0}% — " +
                        "retrying {Attempt}/{Max} at rate={NewRate:F0}%",
                        i + 1, entries.Count, actualMs, expectedMs, rate, overrunAttempt, MaxOverrunRetries, newRate);

                    rate = newRate;
                    var retrySsml = BuildEntrySsml(entry.Text, entryVoice, lang, rate);
                    try
                    {
                        result = await _engine.SpeakSsmlAsync(retrySsml, endpointUrl, subscriptionKey, entryVoice, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "Entry {I}/{Total} overrun retry {Attempt}/{Max} failed ({Msg}) — keeping previous audio",
                            i + 1, entries.Count, overrunAttempt, MaxOverrunRetries, ex.Message);
                        break;
                    }
                }
            }

            rawResults[i] = result;

            if (!formatDetected && result is not null)
            {
                sampleRate     = result.SampleRate;
                channels       = result.Channels;
                bitsPerSample  = result.BitsPerSample;
                formatDetected = true;
            }
        }

        // Upmix mono TTS to stereo when the source video is stereo or surround.
        // Capped at stereo: distributing dialogue across 5.1/7.1 by sample-duplication
        // is unnatural — front L/R stereo is the conventional format for dubbed dialogue.
        var targetChannels = channels == 1 && inputAudioChannels >= 2 ? 2 : channels;

        _logger.LogInformation(
            "TTS audio format: {Rate}Hz {In}ch {Bits}-bit → producing {Out}ch WAV",
            sampleRate, channels, bitsPerSample, targetChannels);

        // Phase 2: assembly — interleave silence in the target format.
        var chunks = new List<byte[]>(entries.Count * 2);
        var curMs  = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry      = entries[i];
            var leadingMs  = Math.Max(0, entry.StartMs - curMs);
            var expectedMs = entry.EndMs - entry.StartMs;

            if (leadingMs > 0)
                chunks.Add(MakeSilenceWav(leadingMs, sampleRate, targetChannels, bitsPerSample));

            int spokenMs;
            var entryResult = rawResults[i];
            if (entryResult is not null)
            {
                var finalAudio = targetChannels > channels
                    ? UpmixMonoToStereo(entryResult.AudioData)
                    : entryResult.AudioData;
                chunks.Add(finalAudio);
                var actualMs = entryResult.DurationMs; // duration unchanged by upmix
                var padMs    = expectedMs - actualMs;
                if (padMs > 50)
                {
                    chunks.Add(MakeSilenceWav(padMs, sampleRate, targetChannels, bitsPerSample));
                    spokenMs = expectedMs;
                }
                else
                {
                    spokenMs = actualMs;

                    if (actualMs > expectedMs + OverrunToleranceMs)
                        _logger.LogWarning(
                            "Entry {I}/{Total} TTS audio ({Actual}ms) still overran its window ({Expected}ms) by {Over}ms after retries",
                            i + 1, entries.Count, actualMs, expectedMs, actualMs - expectedMs);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Entry {I}/{Total} returned invalid audio — substituting {Ms}ms silence",
                    i + 1, entries.Count, expectedMs);
                chunks.Add(MakeSilenceWav(Math.Max(expectedMs, 1), sampleRate, targetChannels, bitsPerSample));
                spokenMs = expectedMs;
            }

            curMs = Math.Max(curMs, entry.StartMs) + spokenMs;
        }

        return ConcatenateWav(chunks);
    }

    private static string BuildEntrySsml(string text, string voiceName, string lang, double rate)
    {
        var escaped = XmlEscape(text);
        var content = Math.Abs(rate - 100.0) < 1.0
            ? escaped
            : $"<prosody rate=\"{FormatRateDelta(rate)}\">{escaped}</prosody>";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" " +
               $"xmlns:mstts=\"http://www.w3.org/2001/mstts\" xml:lang=\"{lang}\">" +
               $"<voice name=\"{voiceName}\">{content}</voice>" +
               $"</speak>";
    }

    // Groups consecutive entries into segments such that no gap between neighbours
    // within a segment exceeds maxGapMs and no segment has more than maxEntries entries.
    internal static List<List<VttEntry>> SplitIntoSegments(
        IReadOnlyList<VttEntry> entries,
        int maxGapMs,
        int maxEntries = int.MaxValue)
    {
        var segments = new List<List<VttEntry>>();
        if (entries.Count == 0) return segments;

        var current = new List<VttEntry> { entries[0] };

        for (int i = 1; i < entries.Count; i++)
        {
            var gap = entries[i].StartMs - entries[i - 1].EndMs;
            if (gap > maxGapMs || current.Count >= maxEntries)
            {
                segments.Add(current);
                current = new List<VttEntry>();
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
    public const double DefaultRatePct = 120.0;
    public const double MaxRatePct     = 120.0;

    internal static string BuildSsml(
        IReadOnlyList<VttEntry> entries,
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
                sb.Append($"<prosody rate=\"{FormatRateDelta(rate)}\">{text}</prosody>");

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

    // Formats an absolute rate (e.g. 105.0, meaning 105%) as the signed relative
    // delta SSML expects (e.g. "+5%"), rather than an absolute percentage.
    internal static string FormatRateDelta(double rate)
    {
        var delta = rate - 100.0;
        return delta >= 0 ? $"+{delta:F0}%" : $"{delta:F0}%";
    }

    // ── VTT parser ────────────────────────────────────────────────────────────

    // The leading "WEBVTT" header naturally falls out: it splits into its own single-line
    // block, which is skipped by the `lines.Length < 2` guard below.
    internal static List<VttEntry> ParseVtt(string content)
    {
        var entries = new List<VttEntry>();
        var blocks  = Regex.Split(content.Replace("\r\n", "\n").Trim(), @"\n\s*\n");

        // Must run BEFORE the `lines.Length < 2` guard below — a per-cue NOTE block
        // ("NOTE Speaker1 (estimated: female)") is a single physical line, so it would
        // otherwise be silently dropped by that guard before ever reaching this check
        // (mirrors SpeakerSampleExtractorService.ParseCuesBySpeaker's ordering).
        string? pendingSpeaker = null;
        foreach (var block in blocks)
        {
            var trimmedBlock = block.Trim();

            if (trimmedBlock.StartsWith("NOTE", StringComparison.Ordinal))
            {
                // Only a single-line note is a per-cue speaker label; the multi-line
                // summary header (if translation preserved it) is not one.
                pendingSpeaker = trimmedBlock.Contains('\n') ? null : ExtractSpeakerLabel(trimmedBlock);
                continue;
            }

            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) { pendingSpeaker = null; continue; }

            var timeLine = Array.Find(lines, l => l.Contains("-->"));
            if (timeLine is null) { pendingSpeaker = null; continue; }

            var m = TimeLineRx.Match(timeLine);
            if (!m.Success) { pendingSpeaker = null; continue; }

            var startMs = ToMs(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            var endMs   = ToMs(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);

            var timeIdx = Array.IndexOf(lines, timeLine);
            var raw  = string.Join(" ", lines.Skip(timeIdx + 1));
            var text = MarkupRx.Replace(raw, "");
            text = text.Replace("&nbsp;", " ").Replace("&amp;", "&");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (!string.IsNullOrEmpty(text))
                entries.Add(new VttEntry(startMs, endMs, text, pendingSpeaker));

            pendingSpeaker = null; // each label note pairs with exactly one following cue
        }

        return entries;
    }

    // Extracts the speaker label from a single-line note like "NOTE Speaker1 (estimated:
    // female)" — i.e. everything between "NOTE " and the next space. Returns null for
    // malformed input (no space found).
    private static string? ExtractSpeakerLabel(string singleLineNote)
    {
        var afterNote = singleLineNote["NOTE".Length..].Trim();
        var spaceIdx = afterNote.IndexOf(' ');
        return spaceIdx > 0 ? afterNote[..spaceIdx] : null;
    }

    // ── WAV utilities ─────────────────────────────────────────────────────────

    // Upmixes a mono PCM WAV to stereo by duplicating each sample into both channels.
    // Works for any bit depth. The output always carries a standard 44-byte header.
    internal static byte[] UpmixMonoToStereo(byte[] monoWav)
    {
        var monoDataOffset  = FindPcmDataOffset(monoWav);
        var monoDataSize    = (int)BitConverter.ToUInt32(monoWav, monoDataOffset - 4);
        var sampleRate      = (int)BitConverter.ToUInt32(monoWav, 24);
        var bitsPerSample   = (int)BitConverter.ToUInt16(monoWav, 34);
        var bytesPerSample  = bitsPerSample / 8;
        const int stereoChannels = 2;
        var stereoDataSize  = monoDataSize * stereoChannels;

        var result = new byte[44 + stereoDataSize];

        "RIFF"u8.CopyTo(result);
        BitConverter.GetBytes((uint)(36 + stereoDataSize)).CopyTo(result, 4);
        "WAVE"u8.CopyTo(result.AsSpan(8));
        "fmt "u8.CopyTo(result.AsSpan(12));
        BitConverter.GetBytes(16u).CopyTo(result, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(result, 20);                              // PCM
        BitConverter.GetBytes((ushort)stereoChannels).CopyTo(result, 22);
        BitConverter.GetBytes((uint)sampleRate).CopyTo(result, 24);
        BitConverter.GetBytes((uint)(sampleRate * stereoChannels * bytesPerSample)).CopyTo(result, 28);
        BitConverter.GetBytes((ushort)(stereoChannels * bytesPerSample)).CopyTo(result, 32);
        BitConverter.GetBytes((ushort)bitsPerSample).CopyTo(result, 34);
        "data"u8.CopyTo(result.AsSpan(36));
        BitConverter.GetBytes((uint)stereoDataSize).CopyTo(result, 40);

        // Write each mono sample to both L and R channels.
        var monoSpan   = monoWav.AsSpan(monoDataOffset, monoDataSize);
        var stereoSpan = result.AsSpan(44);
        for (int i = 0; i < monoSpan.Length; i += bytesPerSample)
        {
            var sample = monoSpan.Slice(i, bytesPerSample);
            sample.CopyTo(stereoSpan.Slice(i * stereoChannels));                           // L
            sample.CopyTo(stereoSpan.Slice(i * stereoChannels + bytesPerSample));          // R
        }

        return result;
    }

    // Generates a silent PCM WAV. Format parameters default to 44100 Hz 16-bit mono
    // but are overridden in SynthesisePerEntryAsync once the actual TTS output format
    // is detected from the first returned audio chunk.
    internal static byte[] MakeSilenceWav(
        int durationMs,
        int sampleRate    = 44100,
        int channels      = 1,
        int bitsPerSample = 16)
    {
        var bytesPerSample = bitsPerSample / 8;
        var byteRate       = sampleRate * channels * bytesPerSample;
        // Use long arithmetic — int × int overflows for gaps longer than ~48 seconds.
        var numSamples = (long)sampleRate * durationMs / 1000;
        var dataSize   = (int)(numSamples * channels * bytesPerSample);
        var wav        = new byte[44 + dataSize];

        "RIFF"u8.CopyTo(wav);
        BitConverter.GetBytes((uint)(36 + dataSize)).CopyTo(wav, 4);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BitConverter.GetBytes(16u).CopyTo(wav, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(wav, 20);                    // PCM
        BitConverter.GetBytes((ushort)channels).CopyTo(wav, 22);
        BitConverter.GetBytes((uint)sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes((uint)byteRate).CopyTo(wav, 28);
        BitConverter.GetBytes((ushort)(channels * bytesPerSample)).CopyTo(wav, 32);
        BitConverter.GetBytes((ushort)bitsPerSample).CopyTo(wav, 34);
        "data"u8.CopyTo(wav.AsSpan(36));
        BitConverter.GetBytes((uint)dataSize).CopyTo(wav, 40);
        // data bytes are already 0 (silence)
        return wav;
    }

    // Returns the duration of a valid WAV in milliseconds.
    // Reads the byte rate from the WAV fmt chunk (offset 28) rather than hardcoding a
    // sample rate, so the calculation is correct regardless of the format Azure returns.
    internal static int GetWavDurationMs(byte[] wav)
    {
        var dataOffset = FindPcmDataOffset(wav);
        var dataSize   = BitConverter.ToUInt32(wav, dataOffset - 4);
        var byteRate   = BitConverter.ToUInt32(wav, 28);
        return byteRate > 0 ? (int)(dataSize * 1000L / byteRate) : 0;
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
        if (chunks.Count == 0) return [];
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

internal sealed record VttEntry(int StartMs, int EndMs, string Text, string? Speaker = null);
