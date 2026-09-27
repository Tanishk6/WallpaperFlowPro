namespace WallpaperFlow.Core.Audio;

/// <summary>In-place iterative radix-2 FFT (no allocations after construction).</summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly int[] _rev;
    private readonly float[] _cos, _sin, _window;

    public int Size => _n;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("FFT size must be a power of two", nameof(n));
        _n = n;
        _rev = new int[n];
        int bits = (int)Math.Log2(n);
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
        _cos = new float[n / 2];
        _sin = new float[n / 2];
        for (int i = 0; i < n / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / n);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / n);
        }
        _window = new float[n];
        for (int i = 0; i < n; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
    }

    /// <summary>Hann-windows <paramref name="input"/> and writes |X[k]| for k in [0, n/2) into <paramref name="magnitudes"/>.</summary>
    public void Magnitudes(ReadOnlySpan<float> input, Span<float> re, Span<float> im, Span<float> magnitudes)
    {
        for (int i = 0; i < _n; i++)
        {
            re[_rev[i]] = (i < input.Length ? input[i] : 0) * _window[i];
            im[_rev[i]] = 0;
        }
        for (int size = 2; size <= _n; size <<= 1)
        {
            int half = size >> 1, step = _n / size;
            for (int start = 0; start < _n; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = _sin[k * step];
                    int a = start + k, b = a + half;
                    float tr = re[b] * wr - im[b] * wi;
                    float ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
            }
        }
        for (int k = 0; k < _n / 2; k++) magnitudes[k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]) * 2f / _n;
    }
}
