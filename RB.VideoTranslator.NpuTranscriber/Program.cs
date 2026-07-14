using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using RB.VideoTranslator.WhisperOnnx;

// Isolated NPU transcription attempt — see the .csproj for why this is a separate process.
// Exit codes: 0 = transcribed via NPU, 2 = no compatible NPU on this device (not an error, just
// nothing to do), 1 = any other failure. WhisperOnnxTranscriberService.cs treats every non-zero
// exit (and any crash) identically: fall back to DirectML/CPU in-process.

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: RB.VideoTranslator.NpuTranscriber.exe <wavPath> <onnxModelDir> <outputJsonPath>");
    return 1;
}

string wavPath = args[0];
string onnxModelDir = args[1];
string outputJsonPath = args[2];

try
{
    var catalog = ExecutionProviderCatalog.GetDefault();
    await catalog.EnsureAndRegisterCertifiedAsync();

    bool hasNpu = OrtEnv.Instance().GetEpDevices()
        .Any(d => d.HardwareDevice.Type == OrtHardwareDeviceType.NPU);
    if (!hasNpu)
    {
        Console.Error.WriteLine("No NPU execution provider available on this device.");
        return 2;
    }

    using var sessionOptions = new SessionOptions();
    sessionOptions.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.PREFER_NPU);

    using var encoder = new InferenceSession(Path.Combine(onnxModelDir, "encoder_model.onnx"), sessionOptions);
    using var decoder = new InferenceSession(Path.Combine(onnxModelDir, "decoder_model.onnx"), sessionOptions);
    using var decoderWithPast = new InferenceSession(
        Path.Combine(onnxModelDir, "decoder_with_past_model.onnx"), sessionOptions);

    var tokenizer = new WhisperTokenizerAdapter(onnxModelDir);
    var runner = new WhisperOnnxRunner(encoder, decoder, decoderWithPast, tokenizer, onnxModelDir);
    var samples = WavPcmReader.ReadMonoSamples(wavPath);
    var result = runner.Transcribe(samples, language: null);

    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });
    await File.WriteAllTextAsync(outputJsonPath, json);

    Console.WriteLine($"Transcribed via NPU: {result.Segments.Count} segments, language={result.Language}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"NPU transcription failed: {ex}");
    return 1;
}
