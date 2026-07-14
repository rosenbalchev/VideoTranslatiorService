using Xunit;
using RB.VideoTranslator.Core.Services;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Tests.Core;

public sealed class VoicePaceModelTests
{
    [Fact]
    public void Fit_ZeroSamples_HasZeroSampleCount()
    {
        var model = VoicePaceModel.Fit([], fallbackCharsPerSecond: 13.0);
        Assert.Equal(0, model.SampleCount);
    }

    [Fact]
    public void Fit_ZeroSamples_PredictsExactlyTheFallbackFormula()
    {
        // With no real samples, the ridge fit collapses exactly to the prior — the plain
        // chars/cps formula with no sentence/comma effect — so this must match what the
        // old EffectiveCharsPerSecond/SpeechRateFor combination would have predicted.
        var model = VoicePaceModel.Fit([], fallbackCharsPerSecond: 13.0);

        var features = new TextFeatures(CharCount: 26, WordCount: 5, SentenceCount: 3, CommaCount: 4);
        var expectedMs = 26 / 13.0 * 1000.0;

        Assert.Equal(expectedMs, model.PredictNaturalMs(features), precision: 3);
    }

    [Fact]
    public void Fit_ZeroSamples_EffectiveCharsPerSecondEqualsFallback()
    {
        var model = VoicePaceModel.Fit([], fallbackCharsPerSecond: 13.0);
        Assert.Equal(13.0, model.EffectiveCharsPerSecond, precision: 3);
    }

    [Fact]
    public void Fit_ReportsRealSampleCountNotIncludingPrior()
    {
        var samples = MakeSamples(count: 7, charsFn: i => 40 + i, cps: 15.0, sentences: 1, commas: 0);
        var model = VoicePaceModel.Fit(samples, fallbackCharsPerSecond: 13.0);

        Assert.Equal(7, model.SampleCount);
    }

    [Fact]
    public void Fit_ManyConsistentSamples_ConvergesTowardObservedPaceNotFallback()
    {
        // 300 samples all implying ~20 chars/sec, far from the 13 chars/sec fallback —
        // with that much real data the fit should land close to 20, not 13.
        var samples = MakeSamples(count: 300, charsFn: i => 20 + i % 40, cps: 20.0, sentences: 1, commas: 0);
        var model = VoicePaceModel.Fit(samples, fallbackCharsPerSecond: 13.0);

        Assert.True(model.EffectiveCharsPerSecond > 18.0,
            $"Expected effective cps close to 20, got {model.EffectiveCharsPerSecond}");
    }

    [Fact]
    public void Fit_FewSamples_StaysCloserToFallbackThanToObservedPace()
    {
        // Only 2 samples — nowhere near enough to overwhelm the regularization prior,
        // so the fit should still sit close to the fallback rather than the (thinly
        // evidenced) 20 chars/sec implied by the samples.
        var samples = MakeSamples(count: 2, charsFn: i => 40 + i * 10, cps: 20.0, sentences: 1, commas: 0);
        var model = VoicePaceModel.Fit(samples, fallbackCharsPerSecond: 13.0);

        Assert.True(model.EffectiveCharsPerSecond < 16.0,
            $"Expected effective cps still close to the 13 fallback, got {model.EffectiveCharsPerSecond}");
    }

    [Fact]
    public void Fit_SamplesWithConsistentSentencePause_RecoversPositiveSentenceCoefficient()
    {
        // True generating model: naturalMs = charCount*50 (20 chars/sec) + sentenceCount*150,
        // with charCount and sentenceCount varied independently across samples so the fit
        // isn't confounded. The fitted sentence coefficient should land in the right
        // ballpark of the true 150ms/sentence pause cost.
        var samples = new List<VoicePaceSample>();
        for (var i = 0; i < 500; i++)
        {
            var chars     = 40 + i % 7 * 5;
            var sentences = 1 + i % 4;
            samples.Add(new VoicePaceSample
            {
                Voice         = "VoiceA",
                CharCount     = chars,
                WordCount     = chars / 6,
                SentenceCount = sentences,
                CommaCount    = 0,
                RateUsed      = 100.0,
                ExpectedMs    = 1000,
                ActualMs      = chars * 50 + sentences * 150,
                NaturalMs     = chars * 50 + sentences * 150,
            });
        }

        var model = VoicePaceModel.Fit(samples, fallbackCharsPerSecond: 13.0);

        Assert.InRange(model.Coefficients[2], 100.0, 200.0);
        // No comma effect was trained in — should stay small.
        Assert.InRange(model.Coefficients[3], -50.0, 50.0);
    }

    [Fact]
    public void Fit_SingleSample_DoesNotThrow()
    {
        var samples = MakeSamples(count: 1, charsFn: _ => 40, cps: 20.0, sentences: 1, commas: 0);
        var exception = Record.Exception(() => VoicePaceModel.Fit(samples, fallbackCharsPerSecond: 13.0));

        Assert.Null(exception);
    }

    private static List<VoicePaceSample> MakeSamples(int count, Func<int, int> charsFn, double cps, int sentences, int commas) =>
        Enumerable.Range(0, count)
            .Select(i =>
            {
                var chars = charsFn(i);
                var naturalMs = chars / cps * 1000.0;
                return new VoicePaceSample
                {
                    Voice         = "VoiceA",
                    CharCount     = chars,
                    WordCount     = chars / 6,
                    SentenceCount = sentences,
                    CommaCount    = commas,
                    RateUsed      = 100.0,
                    ExpectedMs    = 1000,
                    ActualMs      = (int)naturalMs,
                    NaturalMs     = naturalMs,
                };
            })
            .ToList();
}
