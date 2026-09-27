using System.Text.Json;
using WallpaperFlow.Core.AI;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Media;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Import;

public sealed record ImportProgress(int Done, int Total, string Current);
public sealed record ImportResult(List<WallpaperItem> Added, List<string> Duplicates, List<string> Errors)
{
    public static ImportResult Empty() => new(new(), new(), new());
    public string Summary =>
        $"{Added.Count} imported" + (Duplicates.Count > 0 ? $", {Duplicates.Count} already in library" : "") + (Errors.Count > 0 ? $", {Errors.Count} failed" : "");
}

public enum ClockStyle { Minimal, Digital, Analog, Word }

/// <summary>
/// Imports files, folders, zip packages, image collections (→ slideshow), GIF collections, URLs and
/// generated wallpapers (clock, shader). Every file is validated by content; packages are extracted safely.
/// </summary>
public sealed class ImportService
{
    private readonly LibraryRepository _library;
    private readonly MediaProbe _probe;
    private readonly ThumbnailService _thumbs;
    private readonly AiService _ai;
    private readonly SettingsStore _settings;

    public ImportService(LibraryRepository library, MediaProbe probe, ThumbnailService thumbs, AiService ai, SettingsStore settings)
    {
        _library = library;
        _probe = probe;
        _thumbs = thumbs;
        _ai = ai;
        _settings = settings;
    }

    private long MaxFile => _settings.Current.Security.MaxImportFileMb * 1048576;
    private long MaxPackage => _settings.Current.Security.MaxPackageMb * 1048576;

    public async Task<ImportResult> ImportPathsAsync(IEnumerable<string> paths, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var result = ImportResult.Empty();
        var list = paths.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = list[i];
            progress?.Report(new ImportProgress(i, list.Count, Path.GetFileName(p)));
            try
            {
                if (Directory.Exists(p)) await ImportFolderAsync(p, result, ct).ConfigureAwait(false);
                else if (Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)) await ImportZipAsync(p, result, ct).ConfigureAwait(false);
                else if (Path.GetExtension(p).Equals(".wftheme", StringComparison.OrdinalIgnoreCase)) result.Errors.Add($"{Path.GetFileName(p)}: import themes from the Themes page");
                else await ImportFileAsync(p, result, null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Import of {p} failed: {ex.Message}");
                result.Errors.Add($"{Path.GetFileName(p)}: {ex.Message}");
            }
        }
        progress?.Report(new ImportProgress(list.Count, list.Count, ""));
        return result;
    }

    private async Task ImportFolderAsync(string folder, ImportResult result, CancellationToken ct)
    {
        if (WallpaperManifest.Load(folder) != null || File.Exists(Path.Combine(folder, "index.html")))
        {
            await ImportPackageFolderAsync(folder, copy: true, result, ct).ConfigureAwait(false);
            return;
        }
        if (File.Exists(Path.Combine(folder, "project.json")))
        {
            var project = WallpaperEngineImporter.ReadProject(folder);
            if (project != null) { await new WallpaperEngineImporter(this).ImportAsync(project, result, ct).ConfigureAwait(false); return; }
        }

        var files = Directory.EnumerateFiles(folder).ToList();
        var images = files.Where(FileValidator.IsImageExtension).ToList();
        var media = files.Where(FileValidator.IsSupportedMedia).ToList();
        if (images.Count >= 2 && media.Count == 0)
        {
            await ImportSlideshowAsync(folder, images, result, ct).ConfigureAwait(false);
            return;
        }
        // Folder of videos/GIFs: import each and group them in a collection named after the folder.
        var before = result.Added.Count;
        foreach (var f in media) await ImportFileAsync(f, result, null, ct).ConfigureAwait(false);
        var added = result.Added.Skip(before).ToList();
        if (added.Count >= 2)
        {
            var cid = _library.CreateCollection(Path.GetFileName(folder));
            foreach (var w in added) _library.AddToCollection(cid, w.Id);
        }
    }

    public async Task<WallpaperItem?> ImportFileAsync(string path, ImportResult result, string? titleOverride, CancellationToken ct)
    {
        var v = FileValidator.Validate(path, MaxFile);
        if (!v.Ok) { result.Errors.Add($"{Path.GetFileName(path)}: {v.Error}"); return null; }
        if (v.Type == WallpaperType.Slideshow) { result.Errors.Add($"{Path.GetFileName(path)}: still images are imported as a folder (slideshow)"); return null; }

        var hash = DuplicateDetector.QuickHash(path);
        if (_settings.Current.Ai.DetectDuplicatesOnImport && _library.FindByHash(hash) is { } existing)
        {
            result.Duplicates.Add($"{Path.GetFileName(path)} (same as “{existing.Title}”)");
            return null;
        }

        var w = new WallpaperItem
        {
            Title = titleOverride ?? TitleFrom(path),
            Type = v.Type!.Value,
            ContentHash = hash,
            FileSize = new FileInfo(path).Length,
        };
        if (_settings.Current.Library.CopyImportedFiles)
        {
            var dir = Path.Combine(AppPaths.Library, w.Id);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, AppPaths.SafeFileName(Path.GetFileNameWithoutExtension(path)) + Path.GetExtension(path).ToLowerInvariant());
            await CopyAsync(path, dest, ct).ConfigureAwait(false);
            w.Source = dest;
        }
        else w.Source = Path.GetFullPath(path);

        await FinishAsync(w, ct).ConfigureAwait(false);
        result.Added.Add(w);
        return w;
    }

    private static async Task CopyAsync(string src, string dest, CancellationToken ct)
    {
        await using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var o = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        await i.CopyToAsync(o, ct).ConfigureAwait(false);
    }

    private async Task ImportZipAsync(string zip, ImportResult result, CancellationToken ct)
    {
        var v = new FileInfo(zip);
        if (v.Length > MaxPackage) { result.Errors.Add($"{v.Name}: package too large"); return; }
        var id = Guid.NewGuid().ToString("N");
        var dest = Path.Combine(AppPaths.Library, id);
        var skipped = FileValidator.SafeExtract(zip, dest, MaxPackage);
        if (skipped > 0) Log.Warn($"{skipped} blocked file(s) skipped in {v.Name}");
        // Packages often wrap everything in one top-level folder.
        var root = dest;
        var entries = Directory.GetFileSystemEntries(dest);
        if (entries.Length == 1 && Directory.Exists(entries[0])) root = entries[0];
        if (WallpaperManifest.Load(root) != null || File.Exists(Path.Combine(root, "index.html")) || File.Exists(Path.Combine(root, "project.json")))
            await ImportPackageFolderAsync(root, copy: false, result, ct, id, titleFallback: Path.GetFileNameWithoutExtension(zip)).ConfigureAwait(false);
        else
        {
            await ImportFolderAsync(root, result, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Imports an HTML/shader/clock package (folder with wallpaper.json or index.html).</summary>
    public async Task<WallpaperItem?> ImportPackageFolderAsync(string folder, bool copy, ImportResult result, CancellationToken ct,
        string? id = null, string? titleFallback = null, WallpaperManifest? manifestOverride = null, Action<WallpaperItem>? configure = null)
    {
        var manifest = manifestOverride ?? WallpaperManifest.Load(folder) ?? new WallpaperManifest { Title = titleFallback ?? Path.GetFileName(folder) };
        var w = new WallpaperItem
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(manifest.Title) ? titleFallback ?? Path.GetFileName(folder) : manifest.Title,
            Type = manifest.WallpaperType,
            Entry = manifest.Entry,
            Author = manifest.Author,
            Description = manifest.Description,
            Category = manifest.Category,
            Tags = manifest.Tags.ToList(),
            IsAudioReactive = manifest.AudioReactive,
            IsInteractive = manifest.Interactive,
            PropertiesJson = manifest.PropertiesJson,
        };
        if (w.Type is WallpaperType.Video or WallpaperType.Gif or WallpaperType.Apng)
        {
            // Video package: validate the entry like any media file.
            var mediaPath = Path.Combine(folder, manifest.Entry);
            var v = FileValidator.Validate(mediaPath, MaxFile);
            if (!v.Ok) { result.Errors.Add($"{w.Title}: {v.Error}"); return null; }
            w.Type = v.Type ?? w.Type;
        }

        string target = folder;
        if (copy)
        {
            target = Path.Combine(AppPaths.Library, w.Id);
            CopyDirectorySafe(folder, target, MaxPackage);
        }
        w.Source = target;
        if (w.IsMedia && w.Type != WallpaperType.Slideshow)
        {
            // Media packages store the file path directly so the renderer and probe see a file.
            w.Source = Path.Combine(target, manifest.Entry);
            w.Entry = null;
            w.ContentHash = DuplicateDetector.QuickHash(w.Source);
            w.FileSize = new FileInfo(w.Source).Length;
        }
        else
        {
            var entry = Path.Combine(target, w.Entry ?? "index.html");
            if (!AppPaths.IsInside(target, entry) || !File.Exists(entry)) { result.Errors.Add($"{w.Title}: entry file '{w.Entry}' not found"); return null; }
            w.FileSize = DirectorySize(target);
        }
        configure?.Invoke(w);
        if (manifest.AllowNetwork) Log.Info($"'{w.Title}' requests network access; it stays blocked until you allow it in the wallpaper's details.");
        await FinishAsync(w, ct).ConfigureAwait(false);
        result.Added.Add(w);
        return w;
    }

    private async Task ImportSlideshowAsync(string folder, List<string> images, ImportResult result, CancellationToken ct)
    {
        var w = new WallpaperItem { Title = TitleFrom(folder), Type = WallpaperType.Slideshow };
        var dir = Path.Combine(AppPaths.Library, w.Id);
        Directory.CreateDirectory(dir);
        long total = 0;
        foreach (var img in images)
        {
            var v = FileValidator.Validate(img, MaxFile);
            if (!v.Ok) { result.Errors.Add($"{Path.GetFileName(img)}: {v.Error}"); continue; }
            var dest = Path.Combine(dir, AppPaths.SafeFileName(Path.GetFileName(img), 120));
            File.Copy(img, dest, true);
            total += new FileInfo(dest).Length;
        }
        w.Source = dir;
        w.FileSize = total;
        await FinishAsync(w, ct).ConfigureAwait(false);
        result.Added.Add(w);
    }

    public async Task<WallpaperItem> ImportUrlAsync(string url, string? title, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("Enter a full http(s) address");
        var w = new WallpaperItem { Title = string.IsNullOrWhiteSpace(title) ? uri.Host : title!, Type = WallpaperType.Url, Source = uri.ToString(), Category = "Web" };
        w.Tags.Add("web");
        await FinishAsync(w, ct).ConfigureAwait(false);
        return w;
    }

    /// <summary>Creates a clock wallpaper from the bundled template (template files are copied so the page is same-origin).</summary>
    public async Task<WallpaperItem> CreateClockAsync(ClockStyle style, string? title, CancellationToken ct = default)
    {
        var w = new WallpaperItem { Title = title ?? $"{style} clock", Type = WallpaperType.Clock, Entry = "index.html", Category = "Minimal" };
        var dir = Path.Combine(AppPaths.Library, w.Id);
        CopyDirectorySafe(Path.Combine(AppPaths.Runtime, "templates", "clock"), dir, 16 * 1048576);
        var props = new Dictionary<string, object>
        {
            ["style"] = new { type = "combo", value = style.ToString().ToLowerInvariant(), label = "Style", options = new[] { "minimal", "digital", "analog", "word" } },
            ["accent"] = new { type = "color", value = "#7A8CFF", label = "Accent" },
            ["seconds"] = new { type = "bool", value = true, label = "Show seconds" },
            ["h24"] = new { type = "bool", value = true, label = "24-hour" },
            ["date"] = new { type = "bool", value = true, label = "Show date" },
        };
        w.PropertiesJson = JsonSerializer.Serialize(props);
        w.Source = dir;
        w.Tags.AddRange(new[] { "clock", style.ToString().ToLowerInvariant() });
        await FinishAsync(w, ct).ConfigureAwait(false);
        return w;
    }

    /// <summary>Creates a shader wallpaper from Shadertoy-style GLSL (mainImage(out vec4, in vec2)).</summary>
    public async Task<WallpaperItem> CreateShaderAsync(string glsl, string title, bool audioReactive, CancellationToken ct = default)
    {
        if (!glsl.Contains("mainImage")) throw new ArgumentException("The shader must define mainImage(out vec4 fragColor, in vec2 fragCoord)");
        var w = new WallpaperItem { Title = title, Type = WallpaperType.Shader, Entry = "index.html", Category = "Abstract", IsAudioReactive = audioReactive, IsInteractive = true };
        var dir = Path.Combine(AppPaths.Library, w.Id);
        CopyDirectorySafe(Path.Combine(AppPaths.Runtime, "templates", "shader"), dir, 16 * 1048576);
        await File.WriteAllTextAsync(Path.Combine(dir, "shader.frag"), glsl, ct).ConfigureAwait(false);
        w.Source = dir;
        w.Tags.Add("shader");
        await FinishAsync(w, ct).ConfigureAwait(false);
        return w;
    }

    /// <summary>Metadata, thumbnail, persistence; preview clip and analysis continue in the background.</summary>
    private async Task FinishAsync(WallpaperItem w, CancellationToken ct)
    {
        if (w.Type is WallpaperType.Video or WallpaperType.Gif or WallpaperType.Apng)
        {
            var info = await _probe.ProbeAsync(w.ResolveEntryPath(), ct).ConfigureAwait(false);
            if (info != null)
            {
                w.Width = info.Width; w.Height = info.Height; w.Duration = info.Duration; w.Fps = info.Fps;
                w.Codec = info.Codec; w.Container = info.Container; w.Bitrate = info.Bitrate;
            }
            else if (w.Type == WallpaperType.Video) Log.Warn($"No metadata for '{w.Title}' (is ffprobe installed?)");
        }
        else if (w.Type == WallpaperType.Slideshow)
        {
            var first = Directory.EnumerateFiles(w.Source).FirstOrDefault(FileValidator.IsImageExtension);
            if (first != null && await _probe.ProbeAsync(first, ct).ConfigureAwait(false) is { } info) { w.Width = info.Width; w.Height = info.Height; }
        }
        w.Thumbnail = await _thumbs.CreateThumbnailAsync(w, ct).ConfigureAwait(false);
        _library.Upsert(w);

        var analyze = _settings.Current.Ai.AutoTagOnImport;
        _ = Task.Run(async () =>
        {
            try
            {
                var clip = await _thumbs.CreatePreviewClipAsync(w).ConfigureAwait(false);
                if (clip != null) w.PreviewClip = clip;
                if (analyze) await _ai.AnalyzeAsync(w, allowCloud: true).ConfigureAwait(false);
                else _library.Upsert(w);
            }
            catch (Exception ex) { Log.Warn($"Background processing of '{w.Title}' failed: {ex.Message}"); }
        }, CancellationToken.None);
    }

    public static void CopyDirectorySafe(string source, string dest, long maxBytes)
    {
        long total = 0;
        var root = Path.GetFullPath(source);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(file);
            if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint) || FileValidator.BlockedExtensions.Contains(fi.Extension)) continue;
            total += fi.Length;
            if (total > maxBytes) throw new InvalidDataException("Package is larger than the allowed size");
            var rel = Path.GetRelativePath(root, file);
            var target = Path.Combine(dest, rel);
            if (!AppPaths.IsInside(dest, target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static long DirectorySize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { return 0; }
    }

    public static string TitleFrom(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        name = name.Replace('_', ' ').Replace('.', ' ').Trim();
        foreach (var junk in new[] { "1080p", "2160p", "4k", "4K", "60fps", "(1)", "[HD]" }) name = name.Replace(junk, "", StringComparison.OrdinalIgnoreCase);
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? "Untitled" : char.ToUpper(name[0]) + name[1..];
    }
}
