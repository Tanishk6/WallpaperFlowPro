namespace WallpaperFlow.Core.AI;

/// <summary>Pixel statistics over packed RGB24 frames. Pure functions, unit-tested.</summary>
public static class ImageAnalysis
{
    public sealed record ColorStats(double Brightness, double Saturation, double Contrast, IReadOnlyDictionary<string, double> Hues);

    public static ColorStats Colors(byte[] rgb)
    {
        var hues = new Dictionary<string, double>();
        double lum = 0, lum2 = 0, sat = 0;
        int n = rgb.Length / 3;
        if (n == 0) return new ColorStats(0, 0, 0, hues);
        for (int i = 0; i < rgb.Length; i += 3)
        {
            double r = rgb[i] / 255.0, g = rgb[i + 1] / 255.0, b = rgb[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            lum += l; lum2 += l * l;
            double s = max <= 0 ? 0 : (max - min) / max;
            sat += s;
            if (s < 0.2 || max < 0.12) continue; // greys and near-black don't vote for a hue
            double h;
            if (max == r) h = 60 * (((g - b) / (max - min)) % 6);
            else if (max == g) h = 60 * ((b - r) / (max - min) + 2);
            else h = 60 * ((r - g) / (max - min) + 4);
            if (h < 0) h += 360;
            var name = HueName(h);
            hues[name] = hues.GetValueOrDefault(name) + s * max;
        }
        double mean = lum / n;
        double sd = Math.Sqrt(Math.Max(0, lum2 / n - mean * mean));
        double total = hues.Values.Sum();
        var norm = hues.ToDictionary(kv => kv.Key, kv => total > 0 ? kv.Value / total : 0);
        return new ColorStats(mean, sat / n, sd, norm);
    }

    public static string HueName(double h) => h switch
    {
        < 15 or >= 345 => "red",
        < 40 => "orange",
        < 65 => "yellow",
        < 160 => "green",
        < 195 => "teal",
        < 255 => "blue",
        < 290 => "purple",
        _ => "pink",
    };

    /// <summary>Luma plane from RGB24.</summary>
    public static double[] Gray(byte[] rgb, int width, int height)
    {
        var g = new double[width * height];
        for (int i = 0, p = 0; p < g.Length && i + 2 < rgb.Length; i += 3, p++)
            g[p] = 0.299 * rgb[i] + 0.587 * rgb[i + 1] + 0.114 * rgb[i + 2];
        return g;
    }

    /// <summary>Variance of the Laplacian — low values mean a soft/blurry image.</summary>
    public static double Sharpness(double[] gray, int w, int h)
    {
        double sum = 0, sum2 = 0;
        int count = 0;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                double lap = gray[i - w] + gray[i + w] + gray[i - 1] + gray[i + 1] - 4 * gray[i];
                sum += lap; sum2 += lap * lap; count++;
            }
        if (count == 0) return 0;
        double mean = sum / count;
        return sum2 / count - mean * mean;
    }

    /// <summary>64-bit difference hash from a 9×8 downscale of the luma plane.</summary>
    public static ulong DHash(double[] gray, int w, int h)
    {
        var small = new double[9 * 8];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 9; x++)
            {
                int x0 = x * w / 9, x1 = Math.Max(x0 + 1, (x + 1) * w / 9);
                int y0 = y * h / 8, y1 = Math.Max(y0 + 1, (y + 1) * h / 8);
                double s = 0; int c = 0;
                for (int yy = y0; yy < y1 && yy < h; yy++)
                    for (int xx = x0; xx < x1 && xx < w; xx++) { s += gray[yy * w + xx]; c++; }
                small[y * 9 + x] = c > 0 ? s / c : 0;
            }
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, bit++)
                if (small[y * 9 + x] > small[y * 9 + x + 1]) hash |= 1UL << bit;
        return hash;
    }

    public static int Hamming(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
