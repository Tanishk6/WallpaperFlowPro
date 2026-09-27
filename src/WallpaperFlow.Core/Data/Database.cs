using Microsoft.Data.Sqlite;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Data;

/// <summary>Opens the library database (WAL, foreign keys) and runs forward-only migrations.</summary>
public sealed class Database : IDisposable
{
    public const int CurrentVersion = 1;
    public SqliteConnection Connection { get; }
    public object Gate { get; } = new();

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        Connection.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-4096;");
        Migrate();
    }

    public int Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string, object?)[] args)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private void Migrate()
    {
        Exec("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
        var v = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(version), 0) FROM schema_version;") ?? 0);
        if (v >= CurrentVersion) return;
        using var tx = Connection.BeginTransaction();
        if (v < 1)
        {
            Exec(Schema.V1);
            Exec("INSERT INTO schema_version(version) VALUES (1);");
        }
        tx.Commit();
        Log.Info($"Database migrated {v} → {CurrentVersion}");
    }

    /// <summary>Consistent snapshot for backups (safe while the app runs).</summary>
    public void BackupTo(string file)
    {
        if (File.Exists(file)) File.Delete(file);
        lock (Gate) Exec("VACUUM INTO $f;", ("$f", file));
    }

    public void Dispose() => Connection.Dispose();
}

internal static class Schema
{
    public const string V1 = """
CREATE TABLE folders (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    parent_id   INTEGER REFERENCES folders(id) ON DELETE CASCADE,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL
);

CREATE TABLE wallpapers (
    id                TEXT PRIMARY KEY,
    title             TEXT NOT NULL,
    type              TEXT NOT NULL,
    source            TEXT NOT NULL,
    entry             TEXT,
    folder_id         INTEGER REFERENCES folders(id) ON DELETE SET NULL,
    thumbnail         TEXT,
    preview_clip      TEXT,
    width             INTEGER NOT NULL DEFAULT 0,
    height            INTEGER NOT NULL DEFAULT 0,
    duration          REAL NOT NULL DEFAULT 0,
    fps               REAL NOT NULL DEFAULT 0,
    codec             TEXT,
    container         TEXT,
    bitrate           INTEGER NOT NULL DEFAULT 0,
    file_size         INTEGER NOT NULL DEFAULT 0,
    date_added        TEXT NOT NULL,
    last_used         TEXT,
    usage_count       INTEGER NOT NULL DEFAULT 0,
    is_favorite       INTEGER NOT NULL DEFAULT 0,
    is_interactive    INTEGER NOT NULL DEFAULT 0,
    is_audio_reactive INTEGER NOT NULL DEFAULT 0,
    category          TEXT,
    description       TEXT,
    author            TEXT,
    origin            TEXT NOT NULL DEFAULT 'local',
    origin_id         TEXT,
    content_hash      TEXT,
    phash             INTEGER,
    quality_score     REAL,
    quality_flags     TEXT,
    properties_json   TEXT,
    effects_json      TEXT,
    playback_json     TEXT,
    updated_at        TEXT NOT NULL
);
CREATE INDEX ix_wallpapers_folder   ON wallpapers(folder_id);
CREATE INDEX ix_wallpapers_hash     ON wallpapers(content_hash);
CREATE INDEX ix_wallpapers_added    ON wallpapers(date_added);
CREATE INDEX ix_wallpapers_favorite ON wallpapers(is_favorite);
CREATE INDEX ix_wallpapers_origin   ON wallpapers(origin, origin_id);

CREATE TABLE tags (
    id    INTEGER PRIMARY KEY AUTOINCREMENT,
    name  TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE wallpaper_tags (
    wallpaper_id TEXT NOT NULL REFERENCES wallpapers(id) ON DELETE CASCADE,
    tag_id       INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    source       TEXT NOT NULL DEFAULT 'user',   -- user | ai | import
    PRIMARY KEY (wallpaper_id, tag_id)
);
CREATE INDEX ix_wallpaper_tags_tag ON wallpaper_tags(tag_id);

CREATE TABLE collections (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    description TEXT,
    created_at  TEXT NOT NULL
);

CREATE TABLE collection_items (
    collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    wallpaper_id  TEXT NOT NULL REFERENCES wallpapers(id) ON DELETE CASCADE,
    position      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (collection_id, wallpaper_id)
);

CREATE TABLE usage_log (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    wallpaper_id TEXT NOT NULL REFERENCES wallpapers(id) ON DELETE CASCADE,
    used_at      TEXT NOT NULL
);
CREATE INDEX ix_usage_log_time ON usage_log(used_at);

CREATE VIRTUAL TABLE wallpapers_fts USING fts5(
    wallpaper_id UNINDEXED, title, tags, description, category,
    tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3'
);
""";
}
