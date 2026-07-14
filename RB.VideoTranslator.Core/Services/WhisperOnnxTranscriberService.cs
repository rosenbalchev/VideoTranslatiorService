using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;
using RB.VideoTranslator.WhisperOnnx;

namespace RB.VideoTranslator.Core.Services;

public sealed class WhisperOnnxTranscriberService : IWhisperOnnxTranscriberService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<WhisperOnnxTranscriberService> _logger;

    public WhisperOnnxTranscriberService(IProcessRunner processRunner, ILogger<WhisperOnnxTranscriberService> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<WhisperTranscription> TranscribeAsync(
        string wavPath,
        string onnxModelDir,
        string ffmpegPath,
        CancellationToken ct = default)
    {
        string normalizedWavPath = Path.Combine(
            Path.GetDirectoryName(wavPath)!,
            $"{Path.GetFileNameWithoutExtension(wavPath)}_16k.wav");

        await _processRunner.RunAsync(
            ffmpegPath,
            $"-y -i \"{wavPath}\" -ar {MelSpectrogramExtractor.SampleRate} -ac 1 \"{normalizedWavPath}\"",
            ct);

        var npuResult = await TryRunNpuExeAsync(normalizedWavPath, onnxModelDir, ct);
        WhisperTranscriptionResult result = npuResult ?? RunInProcess(normalizedWavPath, onnxModelDir);

        var segments = result.Segments
            .Select(s => new TranscribedSegment(s.Start, s.End, s.Text))
            .ToList();
        return new WhisperTranscription(segments, result.Language);
    }

    /// <summary>
    /// Attempts NPU-accelerated transcription (Intel OpenVINO, Qualcomm QNN, AMD VitisAI — via
    /// Windows ML's ExecutionProviderCatalog) in a fully isolated child process. This isolation
    /// is load-bearing, not just tidiness: ExecutionProviderCatalog has been observed to segfault
    /// (a native access violation, NOT a catchable .NET exception) when the OS has a stale/
    /// mismatched system Windows ML component — something no try/catch in this process could
    /// protect against. A crash in the child process just means this method returns null; it
    /// cannot take down the pipeline. Returns null (not an exception) for every failure mode:
    /// missing exe (non-Windows, or NPU support not built/deployed), no compatible NPU on this
    /// device, timeout, non-zero exit, crash, or malformed output — all treated identically by
    /// the caller, which falls back to the in-process DirectML/CPU path.
    /// </summary>
    internal async Task<WhisperTranscriptionResult?> TryRunNpuExeAsync(
        string normalizedWavPath, string onnxModelDir, CancellationToken ct)
    {
        // Must live in its own subdirectory, not next to this app's own binaries: its Windows ML
        // package bundles a differently-flavored onnxruntime.dll under the same filename as the
        // one Microsoft.ML.OnnxRuntime.DirectML puts here — co-locating them would let one
        // silently overwrite the other on disk (see RB.VideoTranslator.NpuTranscriber.csproj).
        var exePath = Path.Combine(
            AppContext.BaseDirectory, "npu-transcriber", "RB.VideoTranslator.NpuTranscriber.exe");
        if (!File.Exists(exePath))
            return null;

        var outputJsonPath = Path.Combine(
            Path.GetDirectoryName(normalizedWavPath)!,
            $"{Path.GetFileNameWithoutExtension(normalizedWavPath)}_npu_result.json");
        File.Delete(outputJsonPath);

        try
        {
            await _processRunner.RunAsync(
                exePath, $"\"{normalizedWavPath}\" \"{onnxModelDir}\" \"{outputJsonPath}\"", ct);

            if (!File.Exists(outputJsonPath))
                return null;

            var json = await File.ReadAllTextAsync(outputJsonPath, ct);
            return JsonSerializer.Deserialize<WhisperTranscriptionResult>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NPU transcription unavailable, falling back to DirectML/CPU");
            return null;
        }
    }

    private static WhisperTranscriptionResult RunInProcess(string normalizedWavPath, string onnxModelDir)
    {
        var samples = WavPcmReader.ReadMonoSamples(normalizedWavPath);
        var tokenizer = new WhisperTokenizerAdapter(onnxModelDir);

        try
        {
            return Run(samples, onnxModelDir, tokenizer, useGpu: true);
        }
        catch
        {
            return Run(samples, onnxModelDir, tokenizer, useGpu: false);
        }
    }

    private static WhisperTranscriptionResult Run(
        float[] samples, string onnxModelDir, WhisperTokenizerAdapter tokenizer, bool useGpu)
    {
        using var sessionOptions = new SessionOptions();
        if (useGpu)
            // MAX_PERFORMANCE picks the fastest available GPU (correctly prefers a discrete GPU
            // over an integrated one on hybrid-graphics machines — AppendExecutionProvider_DML(0)
            // does not, it just takes DXGI's default adapter order). NPU is never a candidate
            // here: reaching any vendor NPU requires Windows ML's ExecutionProviderCatalog, which
            // only runs in the isolated RB.VideoTranslator.NpuTranscriber process (see
            // TryRunNpuExeAsync) — never call SetEpSelectionPolicy(PREFER_NPU) in this process.
            sessionOptions.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MAX_PERFORMANCE);

        using var encoder = new InferenceSession(Path.Combine(onnxModelDir, "encoder_model.onnx"), sessionOptions);
        using var decoder = new InferenceSession(Path.Combine(onnxModelDir, "decoder_model.onnx"), sessionOptions);
        using var decoderWithPast = new InferenceSession(
            Path.Combine(onnxModelDir, "decoder_with_past_model.onnx"), sessionOptions);

        var runner = new WhisperOnnxRunner(encoder, decoder, decoderWithPast, tokenizer, onnxModelDir);
        return runner.Transcribe(samples, language: null);
    }
}
