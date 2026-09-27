using System.Security.Cryptography;
using WallpaperFlow.Core.Import;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Cloud;

/// <summary>
/// Online wallpaper catalog (static JSON index + downloads). "Marketplace ready": entries already carry
/// price/currency/license so a store backend can serve the same shape; free items download directly.
/// Every download is size-limited and SHA-256 verified before it reaches the importer.
/// </summary>
public sealed class CatalogService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly SettingsStore _settings;
    private readonly ImportService _import;
    private CatalogIndex? _cached;
    private DateTime _cachedAt;

    public CatalogService(SettingsStore settings, ImportService import) { _settings = settings; _import = import; }

    public async Task<CatalogIndex> GetIndexAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cached != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(30)) return _cached;
        var url = _settings.Current.Cloud.CatalogUrl;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The catalog must be served over HTTPS");
        var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        _cached = JsonDefaults.Deserialize<CatalogIndex>(json) ?? new CatalogIndex();
        _cachedAt = DateTime.UtcNow;
        return _cached;
    }

    public async Task<ImportResult> DownloadAsync(CatalogEntry entry, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (entry.Price > 0) throw new InvalidOperationException("Paid items require a store account (not available yet)");
        if (!entry.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Insecure download URL");
        long max = _settings.Current.Security.MaxPackageMb * 1048576;
        if (entry.Size > max) throw new InvalidOperationException("Item is larger than the allowed package size");

        var ext = Path.GetExtension(new Uri(entry.DownloadUrl).AbsolutePath);
        if (string.IsNullOrEmpty(ext)) ext = ".zip";
        Directory.CreateDirectory(AppPaths.Temp);
        var temp = Path.Combine(AppPaths.Temp, $"catalog-{AppPaths.SafeFileName(entry.Id, 40)}{ext}");
        using (var resp = await Http.GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? entry.Size;
            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(temp);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                read += n;
                if (read > max) throw new InvalidDataException("Download exceeded the size limit");
                sha.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                if (total > 0) progress?.Report((double)read / total);
            }
            var hash = Convert.ToHexString(sha.GetHashAndReset());
            if (!string.IsNullOrEmpty(entry.Sha256) && !hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                output.Close();
                File.Delete(temp);
                throw new InvalidDataException("Checksum mismatch — the download was corrupted or tampered with");
            }
        }
        try
        {
            var result = await _import.ImportPathsAsync(new[] { temp }, null, ct).ConfigureAwait(false);
            foreach (var w in result.Added)
            {
                w.Origin = "catalog"; w.OriginId = entry.Id; w.Author = entry.Author; w.Title = entry.Title;
                w.Category ??= entry.Category;
                foreach (var t in entry.Tags) if (!w.Tags.Contains(t)) w.Tags.Add(t);
            }
            return result;
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}
