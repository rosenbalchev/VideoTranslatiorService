using System.Text.RegularExpressions;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Core.Services;

// Extracts the paragraph-level features VoicePaceModel regresses on. Kept separate from
// VttToAzureTtsService's VTT/SSML concerns since it operates on plain cue text, independent
// of subtitle timing or format.
internal static class TextFeatureExtractor
{
    private static readonly Regex WordRx        = new(@"\S+", RegexOptions.Compiled);
    private static readonly Regex SentenceEndRx = new(@"[.!?]+", RegexOptions.Compiled);

    public static TextFeatures Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TextFeatures(0, 0, 0, 0);

        var charCount = text.Length;
        var wordCount = WordRx.Matches(text).Count;

        // Every non-empty paragraph counts as at least one "sentence" for pacing purposes,
        // even if it has no terminal punctuation (e.g. a trailing subtitle fragment).
        var sentenceCount = Math.Max(1, SentenceEndRx.Matches(text).Count);

        var commaCount = text.Count(c => c == ',');

        return new TextFeatures(charCount, wordCount, sentenceCount, commaCount);
    }
}
