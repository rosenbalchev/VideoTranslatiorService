using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Core.Services;

// Learns naturalMs ≈ charCount/cps*1000 + β·[sentenceCount, commaCount] per voice, refit
// from scratch every time it's needed (see VttToAzureTtsService.SynthesisePerEntryAsync)
// rather than maintained incrementally — per-voice sample volume is expected to stay small
// enough that a full refit is cheap and keeps the model trivial to re-derive whenever the
// feature set changes.
//
// Deliberately decomposed into two stages rather than one joint 4-feature ridge fit:
//
//  1. Baseline chars/sec — a simple shrinkage-weighted average of this voice's own
//     observed total chars / total natural ms vs. the fallback prior, converging as
//     sample count grows (same spirit as the old VoicePace ramp).
//  2. Punctuation correction — a ridge regression, regularized toward "no punctuation
//     effect", fit on the RESIDUAL left after subtracting the baseline's prediction.
//
// A single joint fit (naturalMs ~ β·[1, charCount, sentenceCount, commaCount]) was tried
// first and is numerically unsound here: real paragraphs rarely vary charCount down near
// zero, so an intercept term can trade off against the charCount slope almost for free
// while barely raising the fit's regularization cost — even with hundreds of samples, the
// jointly-fit charCount coefficient drifted far from the true pace because the (far more
// weakly regularized) intercept/sentence terms absorbed the trend instead. Estimating the
// baseline chars/sec separately with a robust ratio estimator removes that degree of
// freedom entirely, so the punctuation-only regression that remains has no similarly
// dominant, poorly-conditioned competitor.
internal sealed class VoicePaceModel
{
    // Baseline blend: how many "equivalent samples" the fallback prior is worth before the
    // voice's own observed chars/sec pace takes over (shrinkage weight = n/(n+this)).
    private const double BaselinePriorEquivalentSamples = 10.0;

    // Punctuation regularization strength, expressed as "worth this many real paragraphs".
    private const double PriorWeight = 15.0;

    // A "typical" paragraph shape, used only to scale each punctuation feature's
    // regularization to a comparable magnitude (sentence counts are usually smaller than
    // comma counts, so a single shared regularization constant would under/over-shrink one
    // relative to the other).
    private const double TypicalSentenceCount = 2.0;
    private const double TypicalCommaCount    = 3.0;

    private readonly double _charsPerSecond;
    private readonly double _residualBias;
    private readonly double _sentenceMsPerSentence;
    private readonly double _commaMsPerComma;

    public int SampleCount { get; }

    // Kept in the same shape as the original single-stage design (bias, chars-coefficient,
    // sentence-coefficient, comma-coefficient) purely so callers logging/displaying the fit
    // (see VttToAzureTtsService's summary table) don't need to know about the two-stage split.
    public IReadOnlyList<double> Coefficients =>
        [_residualBias, 1000.0 / _charsPerSecond, _sentenceMsPerSentence, _commaMsPerComma];

    public double EffectiveCharsPerSecond => _charsPerSecond;

    private VoicePaceModel(double charsPerSecond, double residualBias, double sentenceMsPerSentence, double commaMsPerComma, int sampleCount)
    {
        _charsPerSecond        = charsPerSecond;
        _residualBias          = residualBias;
        _sentenceMsPerSentence = sentenceMsPerSentence;
        _commaMsPerComma       = commaMsPerComma;
        SampleCount            = sampleCount;
    }

    public double PredictNaturalMs(TextFeatures f) =>
        f.CharCount * (1000.0 / _charsPerSecond) + _residualBias
        + _sentenceMsPerSentence * f.SentenceCount + _commaMsPerComma * f.CommaCount;

    public static VoicePaceModel Fit(IReadOnlyList<VoicePaceSample> samples, double fallbackCharsPerSecond)
    {
        var charsPerSecond = FitBaselineCharsPerSecond(samples, fallbackCharsPerSecond);
        var (residualBias, sentenceCoef, commaCoef) = FitPunctuationResidual(samples, charsPerSecond);
        return new VoicePaceModel(charsPerSecond, residualBias, sentenceCoef, commaCoef, samples.Count);
    }

    private static double FitBaselineCharsPerSecond(IReadOnlyList<VoicePaceSample> samples, double fallbackCharsPerSecond)
    {
        if (samples.Count == 0) return fallbackCharsPerSecond;

        var totalChars     = samples.Sum(s => (double)s.CharCount);
        var totalNaturalMs = samples.Sum(s => s.NaturalMs);
        var observedCps    = totalNaturalMs > 0 ? totalChars / (totalNaturalMs / 1000.0) : fallbackCharsPerSecond;

        var weight = samples.Count / (samples.Count + BaselinePriorEquivalentSamples);
        return weight * observedCps + (1 - weight) * fallbackCharsPerSecond;
    }

    // Ridge-regresses (naturalMs - the baseline's own prediction) against [1, sentenceCount,
    // commaCount], regularized toward [0, 0, 0] — i.e. "no punctuation effect beyond the
    // baseline pace" is the prior. Returns (bias, sentenceCoefficient, commaCoefficient).
    private static (double Bias, double SentenceCoef, double CommaCoef) FitPunctuationResidual(
        IReadOnlyList<VoicePaceSample> samples, double charsPerSecond)
    {
        const int featureCount = 3; // [1, sentenceCount, commaCount]
        var lambda = new[]
        {
            PriorWeight,
            PriorWeight * TypicalSentenceCount * TypicalSentenceCount,
            PriorWeight * TypicalCommaCount * TypicalCommaCount,
        };

        var xtx = new double[featureCount, featureCount];
        var xty = new double[featureCount];
        for (int i = 0; i < featureCount; i++)
            xtx[i, i] = lambda[i]; // Xty stays 0 here — the prior for all three is zero.

        Span<double> x = stackalloc double[featureCount];
        foreach (var s in samples)
        {
            var residual = s.NaturalMs - s.CharCount * (1000.0 / charsPerSecond);
            x[0] = 1.0; x[1] = s.SentenceCount; x[2] = s.CommaCount;
            for (int i = 0; i < featureCount; i++)
            {
                xty[i] += x[i] * residual;
                for (int j = 0; j < featureCount; j++)
                    xtx[i, j] += x[i] * x[j];
            }
        }

        var beta = SolveLinearSystem(xtx, xty);
        return (beta[0], beta[1], beta[2]);
    }

    // Solves Ax = b via Gauss-Jordan elimination with partial pivoting. A is always
    // invertible here — the diagonal regularization added above keeps it positive-definite
    // regardless of how many real samples were folded in (including zero).
    private static double[] SolveLinearSystem(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();

        for (int col = 0; col < n; col++)
        {
            var pivotRow = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivotRow, col]))
                    pivotRow = row;

            if (pivotRow != col)
            {
                for (int k = 0; k < n; k++) (m[col, k], m[pivotRow, k]) = (m[pivotRow, k], m[col, k]);
                (x[col], x[pivotRow]) = (x[pivotRow], x[col]);
            }

            var pivot = m[col, col];
            for (int k = 0; k < n; k++) m[col, k] /= pivot;
            x[col] /= pivot;

            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                var factor = m[row, col];
                if (factor == 0) continue;
                for (int k = 0; k < n; k++) m[row, k] -= factor * m[col, k];
                x[row] -= factor * x[col];
            }
        }

        return x;
    }
}
