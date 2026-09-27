using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Data;

public sealed record LibraryQuery
{
    public string? Search { get; init; }
    public long? FolderId { get; init; }
    public long? CollectionId { get; init; }
    public bool FavoritesOnly { get; init; }
    public WallpaperType? Type { get; init; }
    public string? Tag { get; init; }
    public string? Category { get; init; }
    public SortField Sort { get; init; } = SortField.DateAdded;
    public bool Descending { get; init; } = true;
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
}

public sealed record FolderInfo(long Id, string Name, long? ParentId, int Count);
public sealed record CollectionInfo(long Id, string Name, int Count, string? Cover);
public sealed record TagInfo(string Name, int Count);
public sealed record UsageEntry(string WallpaperId, DateTime UsedAt);

/// <summary>All library persistence. One connection, serialised by a lock (SQLite + WAL is fast enough that this never contends in practice).</summary>
public sealed class LibraryRepository
{
    private readonly Database _db;
    public event Action? Changed;

    public LibraryRepository(Database db) => _db = db;

    private const string Columns = """
        w.id, w.title, w.type, w.source, w.entry, w.folder_id, w.thumbnail, w.preview_clip, w.width, w.height, w.duration, w.fps,
        w.codec, w.container, w.bitrate, w.file_size, w.date_added, w.last_used, w.usage_count, w.is_favorite, w.is_interactive,
        w.is_audio_reactive, w.category, w.description, w.author, w.origin, w.origin_id, w.content_hash, w.phash, w.quality_score,
        w.quality_flags, w.properties_json, w.effects_json, w.playback_json, w.updated_at,
        (SELECT group_concat(t.name, '|') FROM wallpaper_tags wt JOIN tags t ON t.id = wt.tag_id WHERE wt.wallpaper_id = w.id) AS tag_list
        """;

    // ── reads ────────────────────────────────────────────────────────────────

    public WallpaperItem? Get(string id)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM wallpapers w WHERE w.id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }
    }

    public WallpaperItem? FindByHash(string contentHash) => QueryWhere("w.content_hash = $h", ("$h", contentHash)).FirstOrDefault();
    public WallpaperItem? FindByOrigin(string origin, string originId) =>
        QueryWhere("w.origin = $o AND w.origin_id = $oid", ("$o", origin), ("$oid", originId)).FirstOrDefault();
    public IReadOnlyList<WallpaperItem> All() => QueryWhere("1=1");

    private List<WallpaperItem> QueryWhere(string where, params (string, object?)[] args)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM wallpapers w WHERE {where};";
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<WallpaperItem>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }
    }

    public IReadOnlyList<WallpaperItem> Query(LibraryQuery q)
    {
        var sql = new StringBuilder($"SELECT {Columns} FROM wallpapers w ");
        var where = new List<string>();
        var args = new List<(string, object?)>();

        var fts = BuildFtsQuery(q.Search);
        if (fts != null)
        {
            sql.Append("JOIN wallpapers_fts f ON f.wallpaper_id = w.id ");
            where.Add("wallpapers_fts MATCH $fts");
            args.Add(("$fts", fts));
        }
        if (q.CollectionId is { } cid)
        {
            sql.Append("JOIN collection_items ci ON ci.wallpaper_id = w.id AND ci.collection_id = $cid ");
            args.Add(("$cid", cid));
        }
        if (q.FolderId is { } fid) { where.Add("w.folder_id = $fid"); args.Add(("$fid", fid)); }
        if (q.FavoritesOnly) where.Add("w.is_favorite = 1");
        if (q.Type is { } t) { where.Add("w.type = $type"); args.Add(("$type", t.ToString())); }
        if (!string.IsNullOrEmpty(q.Category)) { where.Add("w.category = $cat COLLATE NOCASE"); args.Add(("$cat", q.Category)); }
        if (!string.IsNullOrEmpty(q.Tag))
        {
            where.Add("EXISTS (SELECT 1 FROM wallpaper_tags wt JOIN tags tg ON tg.id = wt.tag_id WHERE wt.wallpaper_id = w.id AND tg.name = $tag)");
            args.Add(("$tag", q.Tag));
        }
        if (where.Count > 0) sql.Append("WHERE ").Append(string.Join(" AND ", where)).Append(' ');
        var dir = q.Descending ? "DESC" : "ASC";
        var order = q.Sort switch
        {
            SortField.Title => $"w.title COLLATE NOCASE {dir}",
            SortField.FileSize => $"w.file_size {dir}",
            SortField.Resolution => $"(w.width * w.height) {dir}",
            SortField.Usage => $"w.usage_count {dir}, w.last_used DESC",
            SortField.LastUsed => $"w.last_used {dir}",
            SortField.Duration => $"w.duration {dir}",
            _ => $"w.date_added {dir}",
        };
        if (fts != null && string.IsNullOrEmpty(q.Tag)) order = "bm25(wallpapers_fts, 0, 10.0, 5.0, 1.0, 2.0), " + order;
        sql.Append("ORDER BY ").Append(order).Append(" LIMIT $limit OFFSET $offset;");
        args.Add(("$limit", q.Limit));
        args.Add(("$offset", q.Offset));

        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = sql.ToString();
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<WallpaperItem>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }
    }

    /// <summary>User text → safe FTS5 query: every token becomes a quoted prefix term, AND-joined.</summary>
    public static string? BuildFtsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = text.Split(new[] { ' ', '\t', ',', ';', '"', '\'', '(', ')', '*', ':', '^', '-', '+' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Where(t => t.Length > 0).Take(8).ToList();
        if (tokens.Count == 0) return null;
        return string.Join(" AND ", tokens.Select(t => $"\"{t}\"*"));
    }

    public int Count()
    {
        lock (_db.Gate) return Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM wallpapers;"));
    }

    // ── writes ───────────────────────────────────────────────────────────────

    public void Upsert(WallpaperItem w)
    {
        w.UpdatedAt = DateTime.UtcNow;
        lock (_db.Gate)
        {
            using var tx = _db.Connection.BeginTransaction();
            _db.Exec("""
                INSERT INTO wallpapers (id, title, type, source, entry, folder_id, thumbnail, preview_clip, width, height, duration, fps, codec,
                    container, bitrate, file_size, date_added, last_used, usage_count, is_favorite, is_interactive, is_audio_reactive, category,
                    description, author, origin, origin_id, content_hash, phash, quality_score, quality_flags, properties_json, effects_json,
                    playback_json, updated_at)
                VALUES ($id, $title, $type, $source, $entry, $folder, $thumb, $clip, $w, $h, $dur, $fps, $codec, $container, $bitrate, $size,
                    $added, $used, $count, $fav, $inter, $audio, $cat, $desc, $author, $origin, $oid, $hash, $phash, $qs, $qf, $props, $fx, $pb, $upd)
                ON CONFLICT(id) DO UPDATE SET title=excluded.title, type=excluded.type, source=excluded.source, entry=excluded.entry,
                    folder_id=excluded.folder_id, thumbnail=excluded.thumbnail, preview_clip=excluded.preview_clip, width=excluded.width,
                    height=excluded.height, duration=excluded.duration, fps=excluded.fps, codec=excluded.codec, container=excluded.container,
                    bitrate=excluded.bitrate, file_size=excluded.file_size, last_used=excluded.last_used, usage_count=excluded.usage_count,
                    is_favorite=excluded.is_favorite, is_interactive=excluded.is_interactive, is_audio_reactive=excluded.is_audio_reactive,
                    category=excluded.category, description=excluded.description, author=excluded.author, origin=excluded.origin,
                    origin_id=excluded.origin_id, content_hash=excluded.content_hash, phash=excluded.phash, quality_score=excluded.quality_score,
                    quality_flags=excluded.quality_flags, properties_json=excluded.properties_json, effects_json=excluded.effects_json,
                    playback_json=excluded.playback_json, updated_at=excluded.updated_at;
                """,
                ("$id", w.Id), ("$title", w.Title), ("$type", w.Type.ToString()), ("$source", w.Source), ("$entry", w.Entry),
                ("$folder", w.FolderId), ("$thumb", w.Thumbnail), ("$clip", w.PreviewClip), ("$w", w.Width), ("$h", w.Height),
                ("$dur", w.Duration), ("$fps", w.Fps), ("$codec", w.Codec), ("$container", w.Container), ("$bitrate", w.Bitrate),
                ("$size", w.FileSize), ("$added", Iso(w.DateAdded)), ("$used", w.LastUsed is { } lu ? Iso(lu) : null), ("$count", w.UsageCount),
                ("$fav", w.IsFavorite ? 1 : 0), ("$inter", w.IsInteractive ? 1 : 0), ("$audio", w.IsAudioReactive ? 1 : 0),
                ("$cat", w.Category), ("$desc", w.Description), ("$author", w.Author), ("$origin", w.Origin), ("$oid", w.OriginId),
                ("$hash", w.ContentHash), ("$phash", w.PerceptualHash is { } ph ? unchecked((long)ph) : null), ("$qs", w.QualityScore),
                ("$qf", w.QualityFlags), ("$props", w.PropertiesJson), ("$fx", JsonDefaults.Serialize(w.Effects, false)),
                ("$pb", JsonDefaults.Serialize(w.Playback, false)), ("$upd", Iso(w.UpdatedAt)));
            WriteTags(w.Id, w.Tags, "user", replace: true);
            UpdateFts(w);
            tx.Commit();
        }
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_db.Gate)
        {
            _db.Exec("DELETE FROM wallpapers_fts WHERE wallpaper_id = $id;", ("$id", id));
            _db.Exec("DELETE FROM wallpapers WHERE id = $id;", ("$id", id));
            _db.Exec("DELETE FROM tags WHERE id NOT IN (SELECT tag_id FROM wallpaper_tags);");
        }
        Changed?.Invoke();
    }

    public void SetFavorite(string id, bool favorite)
    {
        lock (_db.Gate) _db.Exec("UPDATE wallpapers SET is_favorite = $f, updated_at = $u WHERE id = $id;", ("$f", favorite ? 1 : 0), ("$u", Iso(DateTime.UtcNow)), ("$id", id));
        Changed?.Invoke();
    }

    /// <summary>Adds tags without removing existing ones (used by the AI tagger with source "ai").</summary>
    public void AddTags(string id, IEnumerable<string> tags, string source)
    {
        lock (_db.Gate)
        {
            using var tx = _db.Connection.BeginTransaction();
            WriteTags(id, tags, source, replace: false);
            if (Get(id) is { } w) UpdateFts(w);
            tx.Commit();
        }
        Changed?.Invoke();
    }

    private void WriteTags(string id, IEnumerable<string> tags, string source, bool replace)
    {
        if (replace) _db.Exec("DELETE FROM wallpaper_tags WHERE wallpaper_id = $id;", ("$id", id));
        foreach (var raw in tags.Select(NormalizeTag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _db.Exec("INSERT OR IGNORE INTO tags(name) VALUES ($n);", ("$n", raw));
            _db.Exec("INSERT OR IGNORE INTO wallpaper_tags(wallpaper_id, tag_id, source) SELECT $id, id, $s FROM tags WHERE name = $n;",
                ("$id", id), ("$s", source), ("$n", raw));
        }
    }

    public static string NormalizeTag(string t)
    {
        var s = new string(t.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray());
        return s.Length > 40 ? s[..40] : s;
    }

    private void UpdateFts(WallpaperItem w)
    {
        var tags = Convert.ToString(_db.Scalar(
            "SELECT group_concat(t.name, ' ') FROM wallpaper_tags wt JOIN tags t ON t.id = wt.tag_id WHERE wt.wallpaper_id = $id;", ("$id", w.Id))) ?? "";
        _db.Exec("DELETE FROM wallpapers_fts WHERE wallpaper_id = $id;", ("$id", w.Id));
        _db.Exec("INSERT INTO wallpapers_fts(wallpaper_id, title, tags, description, category) VALUES ($id, $t, $tags, $d, $c);",
            ("$id", w.Id), ("$t", w.Title), ("$tags", tags), ("$d", w.Description ?? ""), ("$c", w.Category ?? ""));
    }

    public void RecordUsage(string id)
    {
        var now = Iso(DateTime.UtcNow);
        lock (_db.Gate)
        {
            _db.Exec("UPDATE wallpapers SET usage_count = usage_count + 1, last_used = $now WHERE id = $id;", ("$now", now), ("$id", id));
            _db.Exec("INSERT INTO usage_log(wallpaper_id, used_at) VALUES ($id, $now);", ("$id", id), ("$now", now));
            _db.Exec("DELETE FROM usage_log WHERE used_at < $cut;", ("$cut", Iso(DateTime.UtcNow.AddDays(-180))));
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<UsageEntry> UsageSince(DateTime since)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT wallpaper_id, used_at FROM usage_log WHERE used_at >= $s;";
            cmd.Parameters.AddWithValue("$s", Iso(since));
            using var r = cmd.ExecuteReader();
            var list = new List<UsageEntry>();
            while (r.Read()) list.Add(new UsageEntry(r.GetString(0), ParseDate(r.GetString(1))));
            return list;
        }
    }

    public IReadOnlyList<TagInfo> Tags()
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT t.name, COUNT(*) FROM tags t JOIN wallpaper_tags wt ON wt.tag_id = t.id GROUP BY t.id ORDER BY COUNT(*) DESC, t.name;";
            using var r = cmd.ExecuteReader();
            var list = new List<TagInfo>();
            while (r.Read()) list.Add(new TagInfo(r.GetString(0), r.GetInt32(1)));
            return list;
        }
    }

    public IReadOnlyList<string> Categories()
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT category FROM wallpapers WHERE category IS NOT NULL AND category <> '' ORDER BY category;";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    // ── folders & collections ────────────────────────────────────────────────

    public long CreateFolder(string name, long? parentId = null)
    {
        lock (_db.Gate)
        {
            _db.Exec("INSERT INTO folders(name, parent_id, created_at) VALUES ($n, $p, $c);", ("$n", name), ("$p", parentId), ("$c", Iso(DateTime.UtcNow)));
            var id = Convert.ToInt64(_db.Scalar("SELECT last_insert_rowid();"));
            Changed?.Invoke();
            return id;
        }
    }

    public IReadOnlyList<FolderInfo> Folders()
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT f.id, f.name, f.parent_id, (SELECT COUNT(*) FROM wallpapers w WHERE w.folder_id = f.id) FROM folders f ORDER BY f.sort_order, f.name;";
            using var r = cmd.ExecuteReader();
            var list = new List<FolderInfo>();
            while (r.Read()) list.Add(new FolderInfo(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetInt32(3)));
            return list;
        }
    }

    public void RenameFolder(long id, string name) { lock (_db.Gate) _db.Exec("UPDATE folders SET name = $n WHERE id = $id;", ("$n", name), ("$id", id)); Changed?.Invoke(); }
    public void DeleteFolder(long id) { lock (_db.Gate) _db.Exec("DELETE FROM folders WHERE id = $id;", ("$id", id)); Changed?.Invoke(); }
    public void MoveToFolder(string wallpaperId, long? folderId)
    {
        lock (_db.Gate) _db.Exec("UPDATE wallpapers SET folder_id = $f WHERE id = $id;", ("$f", folderId), ("$id", wallpaperId));
        Changed?.Invoke();
    }

    public long CreateCollection(string name, string? description = null)
    {
        lock (_db.Gate)
        {
            _db.Exec("INSERT INTO collections(name, description, created_at) VALUES ($n, $d, $c);", ("$n", name), ("$d", description), ("$c", Iso(DateTime.UtcNow)));
            var id = Convert.ToInt64(_db.Scalar("SELECT last_insert_rowid();"));
            Changed?.Invoke();
            return id;
        }
    }

    public IReadOnlyList<CollectionInfo> Collections()
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT c.id, c.name, (SELECT COUNT(*) FROM collection_items ci WHERE ci.collection_id = c.id),
                       (SELECT w.thumbnail FROM collection_items ci JOIN wallpapers w ON w.id = ci.wallpaper_id
                        WHERE ci.collection_id = c.id ORDER BY ci.position LIMIT 1)
                FROM collections c ORDER BY c.name;
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<CollectionInfo>();
            while (r.Read()) list.Add(new CollectionInfo(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)));
            return list;
        }
    }

    public void AddToCollection(long collectionId, string wallpaperId)
    {
        lock (_db.Gate) _db.Exec("""
            INSERT OR IGNORE INTO collection_items(collection_id, wallpaper_id, position)
            VALUES ($c, $w, COALESCE((SELECT MAX(position) + 1 FROM collection_items WHERE collection_id = $c), 0));
            """, ("$c", collectionId), ("$w", wallpaperId));
        Changed?.Invoke();
    }

    public void RemoveFromCollection(long collectionId, string wallpaperId)
    {
        lock (_db.Gate) _db.Exec("DELETE FROM collection_items WHERE collection_id = $c AND wallpaper_id = $w;", ("$c", collectionId), ("$w", wallpaperId));
        Changed?.Invoke();
    }

    public void RenameCollection(long id, string name) { lock (_db.Gate) _db.Exec("UPDATE collections SET name = $n WHERE id = $id;", ("$n", name), ("$id", id)); Changed?.Invoke(); }
    public void DeleteCollection(long id) { lock (_db.Gate) _db.Exec("DELETE FROM collections WHERE id = $id;", ("$id", id)); Changed?.Invoke(); }

    public IReadOnlyList<long> CollectionsOf(string wallpaperId)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT collection_id FROM collection_items WHERE wallpaper_id = $w;";
            cmd.Parameters.AddWithValue("$w", wallpaperId);
            using var r = cmd.ExecuteReader();
            var list = new List<long>();
            while (r.Read()) list.Add(r.GetInt64(0));
            return list;
        }
    }

    // ── mapping ──────────────────────────────────────────────────────────────

    private static WallpaperItem Map(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var w = new WallpaperItem
        {
            Id = r.GetString(0),
            Title = r.GetString(1),
            Type = Enum.TryParse<WallpaperType>(r.GetString(2), out var t) ? t : WallpaperType.Video,
            Source = r.GetString(3),
            Entry = S(4),
            FolderId = r.IsDBNull(5) ? null : r.GetInt64(5),
            Thumbnail = S(6),
            PreviewClip = S(7),
            Width = r.GetInt32(8),
            Height = r.GetInt32(9),
            Duration = r.GetDouble(10),
            Fps = r.GetDouble(11),
            Codec = S(12),
            Container = S(13),
            Bitrate = r.GetInt64(14),
            FileSize = r.GetInt64(15),
            DateAdded = ParseDate(r.GetString(16)),
            LastUsed = r.IsDBNull(17) ? null : ParseDate(r.GetString(17)),
            UsageCount = r.GetInt32(18),
            IsFavorite = r.GetInt32(19) != 0,
            IsInteractive = r.GetInt32(20) != 0,
            IsAudioReactive = r.GetInt32(21) != 0,
            Category = S(22),
            Description = S(23),
            Author = S(24),
            Origin = r.GetString(25),
            OriginId = S(26),
            ContentHash = S(27),
            PerceptualHash = r.IsDBNull(28) ? null : unchecked((ulong)r.GetInt64(28)),
            QualityScore = r.IsDBNull(29) ? null : r.GetDouble(29),
            QualityFlags = S(30),
            PropertiesJson = S(31),
            UpdatedAt = ParseDate(r.GetString(34)),
        };
        try { if (S(32) is { } fx) w.Effects = JsonDefaults.Deserialize<EffectsProfile>(fx) ?? new(); } catch { }
        try { if (S(33) is { } pb) w.Playback = JsonDefaults.Deserialize<PlaybackOverrides>(pb) ?? new(); } catch { }
        var tagList = S(35);
        w.Tags = string.IsNullOrEmpty(tagList) ? new() : tagList.Split('|').ToList();
        return w;
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime ParseDate(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.UtcNow;
}
