using System.IO.Compression;
using System.Text;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Media;

public sealed record ValidationResult(bool Ok, WallpaperType? Type, string? Error)
{
    public static ValidationResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Validates files by content (magic bytes), not by extension: an .mp4 that is really an executable is
/// rejected. Also refuses symlinks/junctions, oversized files, and unpacks packages safely.
/// </summary>
public static class FileValidator
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv" };
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp" };
    public static readonly HashSet<string> AnimatedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".gif", ".apng", ".png" };

    /// <summary>Never extracted from packages. Script files that browsers need (.js, .mjs) are allowed; native code is not.</summary>
    public static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".scr", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".wsf", ".wsh", ".hta", ".msi", ".msp",
        ".lnk", ".url", ".reg", ".cpl", ".msc", ".jar", ".appx", ".msix", ".application", ".gadget", ".inf", ".ocx", ".pif", ".scf",
    };

    public static bool IsImageExtension(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideoExtension(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsSupportedMedia(string path) =>
        IsVideoExtension(path) || Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".apng", StringComparison.OrdinalIgnoreCase);

    public static ValidationResult Validate(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return ValidationResult.Fail("File not found");
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return ValidationResult.Fail("Links and junctions are not imported");
            if (info.Length == 0) return ValidationResult.Fail("File is empty");
            if (info.Length > maxBytes) return ValidationResult.Fail($"File is larger than {maxBytes / 1048576} MB");
            var ext = info.Extension;
            if (BlockedExtensions.Contains(ext)) return ValidationResult.Fail("Executable files are not allowed");

            Span<byte> head = stackalloc byte[64];
            int n;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan))
                n = fs.Read(head);
            var sniffed = Sniff(head[..n], path);
            if (sniffed == null) return ValidationResult.Fail("Unrecognised or unsupported file content");
            if (IsVideoExtension(path) && sniffed != WallpaperType.Video) return ValidationResult.Fail("File content does not match its extension");
            return new ValidationResult(true, sniffed, null);
        }
        catch (Exception ex) { return ValidationResult.Fail(ex.Message); }
    }

    /// <summary>Identifies container by magic number. Returns Slideshow for plain still images.</summary>
    public static WallpaperType? Sniff(ReadOnlySpan<byte> h, string? pathForApng = null)
    {
        if (h.Length >= 12 && (Ascii(h[4..8]) is "ftyp" or "moov" or "mdat" or "free" or "wide" or "skip")) return WallpaperType.Video;
        if (h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3) return WallpaperType.Video;          // Matroska / WebM
        if (h.Length >= 12 && Ascii(h[..4]) == "RIFF" && Ascii(h[8..12]) == "AVI ") return WallpaperType.Video;                  // AVI
        if (h.Length >= 12 && Ascii(h[..4]) == "RIFF" && Ascii(h[8..12]) == "WEBP") return WallpaperType.Slideshow;             // WebP still
        if (h.Length >= 8 && h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75 && h[4] == 0x8E && h[5] == 0x66 && h[6] == 0xCF && h[7] == 0x11)
            return WallpaperType.Video;                                                                                            // ASF / WMV
        if (h.Length >= 6 && Ascii(h[..6]) is "GIF87a" or "GIF89a") return WallpaperType.Gif;
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47)
            return pathForApng != null && IsAnimatedPng(pathForApng) ? WallpaperType.Apng : WallpaperType.Slideshow;
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return WallpaperType.Slideshow;                       // JPEG
        if (h.Length >= 2 && h[0] == 0x42 && h[1] == 0x4D) return WallpaperType.Slideshow;                                        // BMP
        return null;
    }

    private static string Ascii(ReadOnlySpan<byte> b) => Encoding.ASCII.GetString(b);

    /// <summary>APNG = PNG with an acTL chunk before the first IDAT.</summary>
    public static bool IsAnimatedPng(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            fs.Position = 8;
            for (int i = 0; i < 64 && fs.Position + 8 <= fs.Length; i++)
            {
                var lenBytes = br.ReadBytes(4);
                int len = (lenBytes[0] << 24) | (lenBytes[1] << 16) | (lenBytes[2] << 8) | lenBytes[3];
                var type = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (type == "acTL") return true;
                if (type == "IDAT" || len < 0) return false;
                fs.Position += len + 4; // data + CRC
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Extracts a zip with zip-slip, zip-bomb and file-type protection. Returns the number of skipped entries.
    /// </summary>
    public static int SafeExtract(string zipPath, string destination, long maxTotalBytes, int maxEntries = 5000)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > maxEntries) throw new InvalidDataException("Package has too many files");
        long total = 0;
        int skipped = 0;
        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue; // directory entry
            var target = Path.GetFullPath(Path.Combine(destination, e.FullName.Replace('\\', '/')));
            if (!AppPaths.IsInside(destination, target)) throw new InvalidDataException("Package contains an unsafe path: " + e.FullName);
            if (BlockedExtensions.Contains(Path.GetExtension(e.Name))) { skipped++; Log.Warn("Skipped blocked file in package: " + e.FullName); continue; }
            total += e.Length;
            if (total > maxTotalBytes) throw new InvalidDataException("Package expands beyond the size limit");
            if (e.CompressedLength > 0 && e.Length / Math.Max(1, e.CompressedLength) > 200 && e.Length > 10_000_000)
                throw new InvalidDataException("Package looks like a compression bomb");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = e.Open();
            using var output = File.Create(target);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > e.Length + 1024) throw new InvalidDataException("Entry size mismatch in package");
                output.Write(buffer, 0, read);
            }
        }
        return skipped;
    }
}
