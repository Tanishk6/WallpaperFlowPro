using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Cloud;

/// <summary>
/// Cloud sync through any synced folder (OneDrive, Dropbox, Google Drive, a NAS share). We write
/// wallpaperflow-sync.json with library metadata (favourites, tags, collections membership by content hash,
/// effects) and optionally the media itself. Merge is last-write-wins per wallpaper using UpdatedAt.
/// No account, no server: the user's own storage provider does the transport.
/// </summary>
public sealed class FolderSyncService
{
    private readonly LibraryRepository _library;
    private readonly SettingsStore _settings;
    public FolderSyncService(LibraryRepository library, SettingsStore settings) { _library = library; _settings = settings; }

    public sealed class SyncFile
    {
        public int Version { get; set; } = 1;
        public string Machine { get; set; } = Environment.MachineName;
        public DateTime Written { get; set; } = DateTime.UtcNow;
        public List<SyncItem> Items { get; set; } = new();
    }

    public sealed class SyncItem
    {
        public string Key { get; set; } = "";            // content hash, or URL for web wallpapers
        public string Title { get; set; } = "";
        public WallpaperType Type { get; set; }
        public bool Favorite { get; set; }
        public List<string> Tags { get; set; } = new();
        public string? Category { get; set; }
        public EffectsProfile? Effects { get; set; }
        public PlaybackOverrides? Playback { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? MediaFile { get; set; }
    }

    private static string KeyOf(WallpaperItem w) => w.Type == WallpaperType.Url ? "url:" + w.Source : w.ContentHash ?? "";

    public async Task<string> SyncAsync(CancellationToken ct = default)
    {
        var cfg = _settings.Current.Cloud;
        if (string.IsNullOrWhiteSpace(cfg.SyncFolder) || !Directory.Exists(cfg.SyncFolder)) throw new InvalidOperationException("Choose a sync folder first");
        var root = Path.Combine(cfg.SyncFolder, "WallpaperFlow");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "wallpaperflow-sync.json");

        SyncFile remote = File.Exists(file) ? JsonDefaults.Deserialize<SyncFile>(await File.ReadAllTextAsync(file, ct)) ?? new() : new();
        var local = _library.All().Where(w => KeyOf(w) != "").ToList();
        var localByKey = local.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.First());
        int pulled = 0, imported = 0;

        foreach (var r in remote.Items)
        {
            if (localByKey.TryGetValue(r.Key, out var w))
            {
                if (r.UpdatedAt > w.UpdatedAt)
                {
                    w.IsFavorite = r.Favorite; w.Tags = r.Tags.ToList(); w.Category = r.Category;
                    if (r.Effects != null) w.Effects = r.Effects;
                    if (r.Playback != null) w.Playback = r.Playback;
                    _library.Upsert(w);
                    pulled++;
                }
            }
            else if (r.Key.StartsWith("url:"))
            {
                var nw = new WallpaperItem { Title = r.Title, Type = WallpaperType.Url, Source = r.Key[4..], Tags = r.Tags.ToList(), IsFavorite = r.Favorite, Category = r.Category };
                _library.Upsert(nw);
                imported++;
            }
            else if (cfg.SyncMedia && r.MediaFile != null && File.Exists(Path.Combine(root, r.MediaFile)))
            {
                var dir = Path.Combine(AppPaths.Library, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, Path.GetFileName(r.MediaFile));
                File.Copy(Path.Combine(root, r.MediaFile), dest, true);
                var nw = new WallpaperItem { Title = r.Title, Type = r.Type, Source = dest, ContentHash = r.Key, Tags = r.Tags.ToList(), IsFavorite = r.Favorite, Category = r.Category, Origin = "sync", FileSize = new FileInfo(dest).Length };
                _library.Upsert(nw);
                imported++;
            }
        }

        var merged = remote.Items.ToDictionary(i => i.Key);
        foreach (var w in _library.All().Where(w => KeyOf(w) != ""))
        {
            var key = KeyOf(w);
            if (merged.TryGetValue(key, out var existing) && existing.UpdatedAt >= w.UpdatedAt) continue;
            string? mediaFile = existing?.MediaFile;
            if (cfg.SyncMedia && w.IsMedia && File.Exists(w.Source) && mediaFile == null)
            {
                Directory.CreateDirectory(Path.Combine(root, "media"));
                mediaFile = Path.Combine("media", key[..Math.Min(16, key.Length)] + Path.GetExtension(w.Source));
                var target = Path.Combine(root, mediaFile);
                if (!File.Exists(target)) File.Copy(w.Source, target);
            }
            merged[key] = new SyncItem
            {
                Key = key, Title = w.Title, Type = w.Type, Favorite = w.IsFavorite, Tags = w.Tags.ToList(), Category = w.Category,
                Effects = w.Effects, Playback = w.Playback, UpdatedAt = w.UpdatedAt, MediaFile = mediaFile,
            };
        }
        var output = new SyncFile { Items = merged.Values.OrderBy(i => i.Title).ToList() };
        AtomicFile.WriteAllText(file, JsonDefaults.Serialize(output));
        _settings.Update("cloud", s => s.Cloud.LastSync = DateTime.Now);
        var summary = $"Synced: {pulled} updated, {imported} added, {output.Items.Count} total in sync folder";
        Log.Info(summary);
        return summary;
    }
}
