using System.Security.Cryptography;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.AI;

public sealed record DuplicateGroup(IReadOnlyList<WallpaperItem> Items, bool Exact);

public static class DuplicateDetector
{
    /// <summary>
    /// Fast content fingerprint: SHA-256 over size + first and last MiB. Identical files always match;
    /// the chance of different videos colliding on size and both ends is negligible.
    /// </summary>
    public static string QuickHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(fs.Length));
        var buf = new byte[1 << 20];
        int n = fs.Read(buf, 0, buf.Length);
        sha.AppendData(buf, 0, n);
        if (fs.Length > buf.Length * 2L)
        {
            fs.Seek(-buf.Length, SeekOrigin.End);
            n = fs.Read(buf, 0, buf.Length);
            sha.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Groups exact duplicates (same content hash) and near duplicates (dHash within <paramref name="threshold"/> bits, similar duration).</summary>
    public static IReadOnlyList<DuplicateGroup> Find(IReadOnlyList<WallpaperItem> items, int threshold = 6)
    {
        var groups = new List<DuplicateGroup>();
        var used = new HashSet<string>();
        foreach (var g in items.Where(i => !string.IsNullOrEmpty(i.ContentHash)).GroupBy(i => i.ContentHash).Where(g => g.Count() > 1))
        {
            groups.Add(new DuplicateGroup(g.ToList(), true));
            foreach (var i in g) used.Add(i.Id);
        }
        var hashed = items.Where(i => i.PerceptualHash.HasValue && !used.Contains(i.Id)).ToList();
        for (int a = 0; a < hashed.Count; a++)
        {
            if (used.Contains(hashed[a].Id)) continue;
            var group = new List<WallpaperItem> { hashed[a] };
            for (int b = a + 1; b < hashed.Count; b++)
            {
                if (used.Contains(hashed[b].Id)) continue;
                var x = hashed[a]; var y = hashed[b];
                if (x.Type != y.Type) continue;
                if (x.Duration > 0 && y.Duration > 0 && Math.Abs(x.Duration - y.Duration) > 1.0) continue;
                if (ImageAnalysis.Hamming(x.PerceptualHash!.Value, y.PerceptualHash!.Value) <= threshold) group.Add(y);
            }
            if (group.Count > 1)
            {
                groups.Add(new DuplicateGroup(group, false));
                foreach (var i in group) used.Add(i.Id);
            }
        }
        return groups;
    }
}
