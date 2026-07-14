using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace RB.VideoTranslator.WhisperOnnx;

/// <summary>
/// Wraps Microsoft.ML.Tokenizers' BpeTokenizer configured to match Whisper's byte-level GPT-2
/// BPE tokenizer exactly (verified token-for-token against transformers' WhisperProcessor on
/// the same exported model). Also exposes the special token ids the decode loop needs
/// (bos/eot/task/language/timestamp tokens) since Whisper's decoder prompt and stopping
/// condition are built entirely out of those ids, not plain text.
/// </summary>
public sealed class WhisperTokenizerAdapter
{
    // Whisper reuses GPT-2's byte-level BPE pretokenization regex exactly.
    private static readonly Regex Gpt2Regex = new(
        @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled);

    // Whisper's language tokens are exactly "<|xx|>" (a 2-letter ISO 639-1 code) — distinct
    // from task tokens like "<|transcribe|>" and timestamp tokens like "<|0.00|>".
    private static readonly Regex LanguageTokenPattern = new(@"^<\|[a-z]{2}\|>$", RegexOptions.Compiled);

    private readonly BpeTokenizer _tokenizer;
    private readonly Dictionary<string, int> _addedTokens;

    public int StartOfTranscript { get; }
    public int EndOfText { get; }
    public int Translate { get; }
    public int Transcribe { get; }
    public int NoTimestamps { get; }
    public int TimestampBegin { get; }

    /// <summary>Language code (e.g. "en") to token id, for every language Whisper supports.</summary>
    public IReadOnlyDictionary<string, int> LanguageTokens { get; }

    public WhisperTokenizerAdapter(string modelDir)
    {
        _addedTokens = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(Path.Combine(modelDir, "added_tokens.json")))!;

        var options = new BpeOptions(
            Path.Combine(modelDir, "vocab.json"),
            Path.Combine(modelDir, "merges.txt"))
        {
            SpecialTokens = _addedTokens,
            PreTokenizer = new RegexPreTokenizer(Gpt2Regex, _addedTokens),
            UnknownToken = "<|endoftext|>",
            ByteLevel = true,
        };
        _tokenizer = BpeTokenizer.Create(options);

        StartOfTranscript = _addedTokens["<|startoftranscript|>"];
        Translate = _addedTokens["<|translate|>"];
        Transcribe = _addedTokens["<|transcribe|>"];
        NoTimestamps = _addedTokens["<|notimestamps|>"];
        TimestampBegin = _addedTokens["<|0.00|>"];

        // <|endoftext|> is part of the base vocab (id 50257), not added_tokens.json.
        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(Path.Combine(modelDir, "vocab.json")))!;
        EndOfText = vocab["<|endoftext|>"];

        var languageTokens = new Dictionary<string, int>();
        foreach (var (token, id) in _addedTokens)
        {
            if (LanguageTokenPattern.IsMatch(token))
                languageTokens[token[2..^2]] = id;
        }
        LanguageTokens = languageTokens;
    }

    public IReadOnlyList<int> Encode(string text) => _tokenizer.EncodeToIds(text);

    /// <summary>Decodes ids to text, dropping the end-of-text token and everything from
    /// StartOfTranscript upward (language/task/timestamp tokens).</summary>
    public string DecodeText(IEnumerable<int> ids) =>
        _tokenizer.Decode(ids.Where(id => id < EndOfText)).Trim();

    public bool IsTimestampToken(int id) => id >= TimestampBegin;

    public double TimestampSeconds(int id) => (id - TimestampBegin) * 0.02;
}
