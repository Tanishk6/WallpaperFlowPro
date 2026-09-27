namespace WallpaperFlow.Core.Audio;

/// <summary>
/// Turns raw PCM into log-spaced bands (30 Hz – 16 kHz) normalised to 0..1, with automatic gain control
/// and attack/decay smoothing so visualisers look lively at any volume.
/// </summary>
public sealed class SpectrumAnalyzer
{
    private readonly Fft _fft;
    private readonly float[] _re, _im, _mag;
    private readonly int[] _bandStart, _bandEnd;
    private readonly float[] _left, _right;
    private float _peak = 0.02f;
    public int Bands { get; }
    public double Smoothing { get; set; } = 0.6;
    public double Gain { get; set; } = 1.0;

    public SpectrumAnalyzer(int sampleRate, int bands = 64, int fftSize = 1024)
    {
        Bands = bands;
        _fft = new Fft(fftSize);
        _re = new float[fftSize]; _im = new float[fftSize]; _mag = new float[fftSize / 2];
        _left = new float[bands]; _right = new float[bands];
        _bandStart = new int[bands]; _bandEnd = new int[bands];
        double fMin = 30, fMax = Math.Min(16000, sampleRate / 2.0), binHz = (double)sampleRate / fftSize;
        for (int b = 0; b < bands; b++)
        {
            double lo = fMin * Math.Pow(fMax / fMin, (double)b / bands);
            double hi = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / bands);
            _bandStart[b] = Math.Clamp((int)Math.Floor(lo / binHz), 1, fftSize / 2 - 1);
            _bandEnd[b] = Math.Clamp((int)Math.Ceiling(hi / binHz), _bandStart[b] + 1, fftSize / 2);
        }
    }

    public int FftSize => _fft.Size;

    /// <summary>Analyse one block per channel; returns overall level (0..1).</summary>
    public float Process(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        float level = 0;
        level = Math.Max(level, Channel(left, _left));
        level = Math.Max(level, Channel(right, _right));
        return level;
    }

    private float Channel(ReadOnlySpan<float> samples, float[] target)
    {
        _fft.Magnitudes(samples, _re, _im, _mag);
        float framePeak = 0, sum = 0;
        Span<float> raw = stackalloc float[Bands];
        for (int b = 0; b < Bands; b++)
        {
            float v = 0;
            for (int k = _bandStart[b]; k < _bandEnd[b]; k++) v = Math.Max(v, _mag[k]);
            // Gentle high-frequency tilt: music energy falls ~3 dB/octave.
            v *= 1f + b / (float)Bands * 2.5f;
            raw[b] = v;
            framePeak = Math.Max(framePeak, v);
        }
        // AGC: peak follows loud passages quickly and relaxes slowly.
        _peak = framePeak > _peak ? framePeak : Math.Max(0.01f, _peak * 0.995f);
        float attack = 0.6f, decay = (float)Math.Clamp(Smoothing, 0, 0.95);
        for (int b = 0; b < Bands; b++)
        {
            float n = (float)Math.Clamp(raw[b] / _peak * Gain, 0, 1);
            n = MathF.Sqrt(n); // perceptual curve
            target[b] = n > target[b] ? target[b] + (n - target[b]) * attack : target[b] * decay + n * (1 - decay);
            sum += target[b];
        }
        return sum / Bands;
    }

    /// <summary>Decay towards silence when no audio arrives (WASAPI loopback delivers nothing while silent).</summary>
    public void DecaySilence()
    {
        for (int b = 0; b < Bands; b++) { _left[b] *= 0.8f; _right[b] *= 0.8f; }
    }

    public void CopyTo(float[] left, float[] right)
    {
        Array.Copy(_left, left, Math.Min(left.Length, Bands));
        Array.Copy(_right, right, Math.Min(right.Length, Bands));
    }
}
