using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace RB.VideoTranslator.WhisperOnnx;

/// <summary>
/// Drives Whisper's encoder + two-graph (no-cache / with-cache) decoder ONNX export through a
/// greedy autoregressive decode loop, provider-agnostic (the caller supplies already-configured
/// <see cref="InferenceSession"/>s — DirectML, OpenVINO, or plain CPU all work identically here).
/// Mirrors transformers' default (num_beams=1, timestamps enabled) generation behavior closely
/// enough for this pipeline's purposes: suppress_tokens/begin_suppress_tokens masking is applied
/// (matches quality-relevant behavior), but the fuller timestamp-pairing logits processor HF uses
/// is not — timestamp tokens are simply read back to build segment boundaries. Audio longer than
/// 30s is chunked sequentially (non-overlapping), unlike HF's overlapping-stride pipeline.
/// </summary>
public sealed class WhisperOnnxRunner
{
    private const int SampleRate = MelSpectrogramExtractor.SampleRate;
    private const int ChunkSamples = SampleRate * 30;
    private const int EncoderSequenceLength = 1500;
    private const int NumLayers = 24;
    private const int NumHeads = 16;
    private const int HeadDim = 64;
    private const int HiddenSize = 1024;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _decoderWithPast;
    private readonly WhisperTokenizerAdapter _tokenizer;
    private readonly MelSpectrogramExtractor _melExtractor = new();
    private readonly int _maxLength;
    private readonly HashSet<int> _suppressTokens;
    private readonly HashSet<int> _beginSuppressTokens;

    public WhisperOnnxRunner(
        InferenceSession encoder,
        InferenceSession decoder,
        InferenceSession decoderWithPast,
        WhisperTokenizerAdapter tokenizer,
        string modelDir)
    {
        _encoder = encoder;
        _decoder = decoder;
        _decoderWithPast = decoderWithPast;
        _tokenizer = tokenizer;

        var generationConfig = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(Path.Combine(modelDir, "generation_config.json")));
        _maxLength = generationConfig.TryGetProperty("max_length", out var maxLenEl) ? maxLenEl.GetInt32() : 448;
        _suppressTokens = ReadIntArray(generationConfig, "suppress_tokens");
        _beginSuppressTokens = ReadIntArray(generationConfig, "begin_suppress_tokens");
    }

    private static HashSet<int> ReadIntArray(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var arr)
            ? [.. arr.EnumerateArray().Select(e => e.GetInt32())]
            : [];

    public WhisperTranscriptionResult Transcribe(ReadOnlySpan<float> audio, string? language)
    {
        var segments = new List<WhisperSegment>();
        string? detectedLanguage = language;

        if (audio.Length == 0)
            return new WhisperTranscriptionResult(segments, detectedLanguage ?? "en");

        int chunkIndex = 0;
        for (int offset = 0; offset < audio.Length; offset += ChunkSamples, chunkIndex++)
        {
            var chunk = audio.Slice(offset, Math.Min(ChunkSamples, audio.Length - offset));

            var mel = _melExtractor.ComputeLogMelSpectrogram(chunk);
            var encoderHidden = RunEncoder(mel);

            detectedLanguage ??= DetectLanguage(encoderHidden);

            double chunkOffsetSeconds = chunkIndex * 30.0;
            var tokens = DecodeChunk(encoderHidden, detectedLanguage);
            segments.AddRange(BuildSegments(tokens, chunkOffsetSeconds, chunkDurationSeconds: chunk.Length / (double)SampleRate));
        }

        return new WhisperTranscriptionResult(segments, detectedLanguage ?? "en");
    }

    private DenseTensor<float> RunEncoder(float[,] mel)
    {
        var data = new float[MelSpectrogramExtractor.NMels * 3000];
        for (int m = 0; m < MelSpectrogramExtractor.NMels; m++)
            for (int t = 0; t < 3000; t++)
                data[m * 3000 + t] = mel[m, t];

        var inputTensor = new DenseTensor<float>(data, [1, MelSpectrogramExtractor.NMels, 3000]);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_features", inputTensor) };

        using var results = _encoder.Run(inputs);
        var hidden = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();
        return CopyTensor(hidden); // detach from the disposable results collection
    }

    private string DetectLanguage(DenseTensor<float> encoderHidden)
    {
        var promptTensor = new DenseTensor<long>(new long[] { _tokenizer.StartOfTranscript }, [1, 1]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", promptTensor),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden),
        };

        using var results = _decoder.Run(inputs);
        var logits = results.First(r => r.Name == "logits").AsTensor<float>(); // [1,1,vocab]

        int bestId = -1;
        float bestScore = float.MinValue;
        foreach (var (_, id) in _tokenizer.LanguageTokens)
        {
            float score = logits[0, 0, id];
            if (score > bestScore) { bestScore = score; bestId = id; }
        }

        return _tokenizer.LanguageTokens.First(kv => kv.Value == bestId).Key;
    }

    private List<int> DecodeChunk(DenseTensor<float> encoderHidden, string language)
    {
        int languageToken = _tokenizer.LanguageTokens.TryGetValue(language, out var lt) ? lt : _tokenizer.LanguageTokens["en"];
        long[] prompt = [_tokenizer.StartOfTranscript, languageToken, _tokenizer.Transcribe];

        var promptTensor = new DenseTensor<long>(prompt, [1, prompt.Length]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", promptTensor),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden),
        };

        using var firstResults = _decoder.Run(inputs);
        var firstLogits = firstResults.First(r => r.Name == "logits").AsTensor<float>();
        int lastPos = prompt.Length - 1;
        int firstToken = ArgmaxWithSuppression(firstLogits, position: lastPos, suppressBegin: true);

        var tokens = new List<int> { firstToken };

        // Carry decoder self-attention KV forward (grows each step); cross-attention (encoder)
        // KV is computed once here and reused unchanged for every subsequent decode step.
        var pastDecoderKey = new DenseTensor<float>[NumLayers];
        var pastDecoderValue = new DenseTensor<float>[NumLayers];
        var encoderKey = new DenseTensor<float>[NumLayers];
        var encoderValue = new DenseTensor<float>[NumLayers];
        for (int i = 0; i < NumLayers; i++)
        {
            pastDecoderKey[i] = CopyTensor(firstResults.First(r => r.Name == $"present.{i}.decoder.key").AsTensor<float>());
            pastDecoderValue[i] = CopyTensor(firstResults.First(r => r.Name == $"present.{i}.decoder.value").AsTensor<float>());
            encoderKey[i] = CopyTensor(firstResults.First(r => r.Name == $"present.{i}.encoder.key").AsTensor<float>());
            encoderValue[i] = CopyTensor(firstResults.First(r => r.Name == $"present.{i}.encoder.value").AsTensor<float>());
        }

        while (tokens.Count < _maxLength - prompt.Length && tokens[^1] != _tokenizer.EndOfText)
        {
            var stepInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(new long[] { tokens[^1] }, [1, 1])),
            };
            for (int i = 0; i < NumLayers; i++)
            {
                stepInputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{i}.decoder.key", pastDecoderKey[i]));
                stepInputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{i}.decoder.value", pastDecoderValue[i]));
                stepInputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{i}.encoder.key", encoderKey[i]));
                stepInputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{i}.encoder.value", encoderValue[i]));
            }

            using var stepResults = _decoderWithPast.Run(stepInputs);
            var stepLogits = stepResults.First(r => r.Name == "logits").AsTensor<float>(); // [1,1,vocab]
            int nextToken = ArgmaxWithSuppression(stepLogits, position: 0, suppressBegin: false);
            tokens.Add(nextToken);

            for (int i = 0; i < NumLayers; i++)
            {
                pastDecoderKey[i] = CopyTensor(stepResults.First(r => r.Name == $"present.{i}.decoder.key").AsTensor<float>());
                pastDecoderValue[i] = CopyTensor(stepResults.First(r => r.Name == $"present.{i}.decoder.value").AsTensor<float>());
            }

            if (nextToken == _tokenizer.EndOfText)
                break;
        }

        return tokens;
    }

    private int ArgmaxWithSuppression(Tensor<float> logits, int position, bool suppressBegin)
    {
        int vocabSize = logits.Dimensions[^1];
        int bestId = -1;
        float bestScore = float.MinValue;
        for (int id = 0; id < vocabSize; id++)
        {
            if (_suppressTokens.Contains(id)) continue;
            if (suppressBegin && _beginSuppressTokens.Contains(id)) continue;

            float score = logits[0, position, id];
            if (score > bestScore) { bestScore = score; bestId = id; }
        }
        return bestId;
    }

    private static DenseTensor<float> CopyTensor(Tensor<float> tensor) =>
        new(tensor.ToArray(), tensor.Dimensions.ToArray());

    private List<WhisperSegment> BuildSegments(List<int> tokens, double chunkOffsetSeconds, double chunkDurationSeconds)
    {
        var segments = new List<WhisperSegment>();
        double? segmentStart = null;
        var textTokens = new List<int>();

        void Flush(double end)
        {
            if (segmentStart is double start && textTokens.Count > 0)
            {
                string text = _tokenizer.DecodeText(textTokens);
                if (!string.IsNullOrWhiteSpace(text))
                    segments.Add(new WhisperSegment(chunkOffsetSeconds + start, chunkOffsetSeconds + end, text));
            }
            textTokens.Clear();
        }

        foreach (int id in tokens)
        {
            if (_tokenizer.IsTimestampToken(id))
            {
                double t = _tokenizer.TimestampSeconds(id);
                if (segmentStart is null)
                {
                    segmentStart = t;
                }
                else
                {
                    Flush(t);
                    segmentStart = t;
                }
            }
            else if (id != _tokenizer.EndOfText)
            {
                textTokens.Add(id);
            }
        }

        // No closing timestamp (e.g. cut off by max_length) — close out at the chunk boundary
        // rather than leaving an unterminated segment (a prior Python-side bug crashed on
        // exactly this: a None end timestamp reaching vtt_common.format_time).
        Flush(segmentStart.HasValue ? chunkDurationSeconds : chunkDurationSeconds);

        return segments;
    }
}
