using Xunit;
using RB.VideoTranslator.Core.Services;

namespace RB.VideoTranslator.Tests.Core;

public sealed class TextFeatureExtractorTests
{
    [Fact]
    public void Extract_EmptyText_ReturnsAllZeros()
    {
        var f = TextFeatureExtractor.Extract("");

        Assert.Equal(0, f.CharCount);
        Assert.Equal(0, f.WordCount);
        Assert.Equal(0, f.SentenceCount);
        Assert.Equal(0, f.CommaCount);
    }

    [Fact]
    public void Extract_WhitespaceOnlyText_ReturnsAllZeros()
    {
        var f = TextFeatureExtractor.Extract("   ");

        Assert.Equal(0, f.CharCount);
        Assert.Equal(0, f.SentenceCount);
    }

    [Fact]
    public void Extract_CharCount_MatchesTextLength()
    {
        var f = TextFeatureExtractor.Extract("Hello world");
        Assert.Equal(11, f.CharCount);
    }

    [Fact]
    public void Extract_WordCount_CountsWhitespaceSeparatedTokens()
    {
        var f = TextFeatureExtractor.Extract("Hello brave new world");
        Assert.Equal(4, f.WordCount);
    }

    [Fact]
    public void Extract_SentenceCount_CountsTerminalPunctuationRuns()
    {
        var f = TextFeatureExtractor.Extract("Hello there. How are you? Great!");
        Assert.Equal(3, f.SentenceCount);
    }

    [Fact]
    public void Extract_SentenceCount_CollapsesConsecutiveTerminalPunctuation()
    {
        var f = TextFeatureExtractor.Extract("Wait... what?!");
        Assert.Equal(2, f.SentenceCount);
    }

    [Fact]
    public void Extract_SentenceCount_FlooredAtOneForFragmentWithNoTerminalPunctuation()
    {
        var f = TextFeatureExtractor.Extract("just a trailing fragment");
        Assert.Equal(1, f.SentenceCount);
    }

    [Fact]
    public void Extract_CommaCount_CountsCommas()
    {
        var f = TextFeatureExtractor.Extract("First, second, third, and fourth");
        Assert.Equal(3, f.CommaCount);
    }

    [Fact]
    public void Extract_CommaCount_ZeroWhenNoCommas()
    {
        var f = TextFeatureExtractor.Extract("No commas here at all");
        Assert.Equal(0, f.CommaCount);
    }
}
