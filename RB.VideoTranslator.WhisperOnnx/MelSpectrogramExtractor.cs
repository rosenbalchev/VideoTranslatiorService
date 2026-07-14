namespace RB.VideoTranslator.WhisperOnnx;

/// <summary>
/// Reimplements HF transformers' WhisperFeatureExtractor log-mel spectrogram computation
/// (80 mel bins, 400-sample/160-hop Hann-windowed frames, Slaney mel scale, log10 + clamp
/// + (x+4)/4 normalization) so the C# decode path sees byte-for-byte the same encoder input
/// distribution the ONNX model was trained/exported against.
///
/// STFT is computed via direct DFT matrix multiplication rather than an FFT: n_fft=400 is not
/// a power of 2, and zero-padding to the next power of 2 would shift the frequency bins the
/// mel filterbank matrix expects. A direct 400-point real DFT is exact and, at ~3000 frames per
/// 30s chunk, cheap (~240M multiply-adds, well under a second on any modern CPU).
/// </summary>
public sealed class MelSpectrogramExtractor
{
    public const int SampleRate = 16000;
    public const int NFft = 400;
    public const int HopLength = 160;
    public const int NMels = 80;
    private const int NFreqBins = NFft / 2 + 1; // 201

    private readonly float[] _window;
    private readonly float[,] _melFilters; // [NMels, NFreqBins]
    private readonly float[,] _dftCos;      // [NFreqBins, NFft]
    private readonly float[,] _dftSin;      // [NFreqBins, NFft]

    public MelSpectrogramExtractor()
    {
        _window = HannWindowPeriodic(NFft);
        _melFilters = BuildSlaneyMelFilterBank(SampleRate, NFft, NMels);
        (_dftCos, _dftSin) = BuildDftMatrices(NFft, NFreqBins);
    }

    /// <summary>
    /// Computes the log-mel spectrogram for up to a 30s window. <paramref name="samples"/>
    /// longer than 30s should be pre-chunked by the caller; shorter audio is zero-padded to
    /// exactly 3000 frames, matching WhisperFeatureExtractor's fixed-size behavior.
    /// </summary>
    /// <returns>[NMels, 3000] log-mel features, row-major (mel-major).</returns>
    public float[,] ComputeLogMelSpectrogram(ReadOnlySpan<float> samples)
    {
        const int targetFrames = 3000; // 30s * 16000 / 160
        const int waveformLength = targetFrames * HopLength; // 480000 = 30s, zero-padded/trimmed
        const int reflectPad = NFft / 2; // 200 — librosa/HF's center=True STFT convention

        // 1. Zero-pad/trim the raw waveform to exactly 30s first (matches
        //    WhisperFeatureExtractor's default padding="max_length" behavior — short inputs
        //    are zero-padded, not just reflected, so silence fills the remainder).
        var waveform = new float[waveformLength];
        samples[..Math.Min(samples.Length, waveformLength)].CopyTo(waveform);

        // 2. Reflect-pad by n_fft/2 on each side so frame t is centered on sample t*hop in the
        //    original coordinates (librosa/HF's center=True convention) instead of left-aligned.
        var padded = new float[waveformLength + 2 * reflectPad];
        Array.Copy(waveform, 0, padded, reflectPad, waveformLength);
        for (int i = 0; i < reflectPad; i++)
        {
            padded[reflectPad - 1 - i] = waveform[Math.Min(i + 1, waveformLength - 1)];
            padded[reflectPad + waveformLength + i] = waveform[Math.Max(waveformLength - 2 - i, 0)];
        }

        var magnitudes = new float[NFreqBins, targetFrames];
        var frame = new float[NFft];

        for (int t = 0; t < targetFrames; t++)
        {
            int start = t * HopLength;
            for (int n = 0; n < NFft; n++)
                frame[n] = padded[start + n] * _window[n];

            for (int k = 0; k < NFreqBins; k++)
            {
                double re = 0, im = 0;
                for (int n = 0; n < NFft; n++)
                {
                    re += frame[n] * _dftCos[k, n];
                    im += frame[n] * _dftSin[k, n];
                }
                magnitudes[k, t] = (float)(re * re + im * im); // power spectrum
            }
        }

        var logMel = new float[NMels, targetFrames];
        float maxVal = float.MinValue;
        for (int m = 0; m < NMels; m++)
        {
            for (int t = 0; t < targetFrames; t++)
            {
                double melEnergy = 0;
                for (int k = 0; k < NFreqBins; k++)
                    melEnergy += _melFilters[m, k] * magnitudes[k, t];

                float logVal = (float)Math.Log10(Math.Max(melEnergy, 1e-10));
                logMel[m, t] = logVal;
                if (logVal > maxVal) maxVal = logVal;
            }
        }

        float floor = maxVal - 8.0f;
        for (int m = 0; m < NMels; m++)
            for (int t = 0; t < targetFrames; t++)
                logMel[m, t] = (Math.Max(logMel[m, t], floor) + 4.0f) / 4.0f;

        return logMel;
    }

    private static float[] HannWindowPeriodic(int length)
    {
        // periodic Hann: 0.5 - 0.5*cos(2*pi*n/N) for n=0..N-1 (matches HF's
        // audio_utils.window_function(..., periodic=True) default used by WhisperFeatureExtractor).
        var window = new float[length];
        for (int n = 0; n < length; n++)
            window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / length));
        return window;
    }

    private static (float[,] cos, float[,] sin) BuildDftMatrices(int nFft, int nFreqBins)
    {
        var cos = new float[nFreqBins, nFft];
        var sin = new float[nFreqBins, nFft];
        for (int k = 0; k < nFreqBins; k++)
        {
            for (int n = 0; n < nFft; n++)
            {
                double angle = -2 * Math.PI * k * n / nFft;
                cos[k, n] = (float)Math.Cos(angle);
                sin[k, n] = (float)Math.Sin(angle);
            }
        }
        return (cos, sin);
    }

    /// <summary>
    /// Slaney-style mel filterbank, matching librosa's default (htk=False, norm="slaney") and
    /// HF audio_utils.mel_filter_bank(..., norm="slaney", mel_scale="slaney").
    /// </summary>
    private static float[,] BuildSlaneyMelFilterBank(int sampleRate, int nFft, int nMels)
    {
        int nFreqBins = nFft / 2 + 1;
        double maxFreq = sampleRate / 2.0;

        double minMel = HzToMelSlaney(0.0);
        double maxMel = HzToMelSlaney(maxFreq);

        var melPoints = new double[nMels + 2];
        for (int i = 0; i < melPoints.Length; i++)
            melPoints[i] = minMel + (maxMel - minMel) * i / (nMels + 1);

        var hzPoints = new double[nMels + 2];
        for (int i = 0; i < hzPoints.Length; i++)
            hzPoints[i] = MelToHzSlaney(melPoints[i]);

        var fftFreqs = new double[nFreqBins];
        for (int k = 0; k < nFreqBins; k++)
            fftFreqs[k] = k * sampleRate / (double)nFft;

        var filters = new float[nMels, nFreqBins];
        for (int m = 0; m < nMels; m++)
        {
            double left = hzPoints[m];
            double center = hzPoints[m + 1];
            double right = hzPoints[m + 2];

            // Slaney-normalized triangular filter: area-normalized so each band contributes
            // equal energy regardless of its width (2 / (right - left)).
            double enorm = 2.0 / (right - left);

            for (int k = 0; k < nFreqBins; k++)
            {
                double freq = fftFreqs[k];
                double upSlope = (freq - left) / (center - left);
                double downSlope = (right - freq) / (right - center);
                double weight = Math.Max(0.0, Math.Min(upSlope, downSlope));
                filters[m, k] = (float)(weight * enorm);
            }
        }

        return filters;
    }

    private static readonly double Logstep = Math.Log(6.4) / 27.0;

    private static double HzToMelSlaney(double hz)
    {
        const double minLogHz = 1000.0;
        const double minLogMel = minLogHz / (200.0 / 3.0); // = 15.0

        if (hz < minLogHz)
            return hz / (200.0 / 3.0);
        return minLogMel + Math.Log(hz / minLogHz) / Logstep;
    }

    private static double MelToHzSlaney(double mel)
    {
        const double minLogHz = 1000.0;
        const double minLogMel = minLogHz / (200.0 / 3.0); // = 15.0

        if (mel < minLogMel)
            return mel * (200.0 / 3.0);
        return minLogHz * Math.Exp(Logstep * (mel - minLogMel));
    }
}
