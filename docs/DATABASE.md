# Database

The library lives in `%LocalAppData%\WallpaperFlow\Data\library.db` (SQLite 3). It runs with WAL journaling,
`synchronous=NORMAL` and foreign keys on. Migrations are forward-only and tracked in `schema_version`, in
`Data/Database.cs`.

## Tables

**wallpapers** holds one row per wallpaper.

| Column | Type | Notes |
|---|---|---|
| id | TEXT PK | 32-char GUID |
| title, type, source, entry | TEXT | `type` is Video, Gif, Apng, Html, Url, Shader, Slideshow or Clock. `source` is a file, a folder or a URL. `entry` is the file inside a package. |
| folder_id | INTEGER → folders | nullable |
| thumbnail, preview_clip | TEXT | file paths |
| width, height, duration, fps, codec, container, bitrate, file_size | | from ffprobe |
| date_added, last_used, updated_at | TEXT | ISO 8601 UTC |
| usage_count, is_favorite, is_interactive, is_audio_reactive | INTEGER | |
| category, description, author | TEXT | |
| origin, origin_id | TEXT | `local`, `catalog`, `wallpaper-engine` or `sync`, plus the external id |
| content_hash | TEXT | SHA-256 of size + first and last MiB (exact duplicates) |
| phash | INTEGER | 64-bit dHash (near duplicates) |
| quality_score, quality_flags | REAL, TEXT | 0–100 and a readable list of flags |
| properties_json | TEXT | user properties (Wallpaper Engine format) |
| effects_json, playback_json | TEXT | `EffectsProfile` and `PlaybackOverrides` |

The remaining tables are:

- **folders:** `id, name, parent_id → folders (cascade), sort_order, created_at`. Folders can nest, and each wallpaper is in at most one.
- **collections:** `id, name, description, created_at`. **collection_items:** `collection_id, wallpaper_id, position`. A wallpaper can be in many collections.
- **tags:** `id, name UNIQUE NOCASE`. **wallpaper_tags:** `wallpaper_id, tag_id, source` (`user`, `ai` or `import`). Orphaned tags are removed when a wallpaper is deleted.
- **usage_log:** `wallpaper_id, used_at`. It feeds recommendations with a 14-day half-life, and entries are pruned after 180 days.

Indexes cover `folder_id`, `content_hash`, `date_added`, `is_favorite`, `(origin, origin_id)`, `wallpaper_tags(tag_id)`
and `usage_log(used_at)`.

## Search

`wallpapers_fts` is an FTS5 table over title, tags, description and category, with the `unicode61
remove_diacritics 2` tokenizer and 2- and 3-character prefix indexes. It is kept in sync by the repository on
every write, rather than by triggers, because the tags column is denormalised.

User input is never passed to FTS directly. `BuildFtsQuery` splits the text, strips operator characters, quotes
every token and adds a prefix wildcard, so `neon cit` becomes `"neon"* AND "cit"*`. Results are ranked with
`bm25` weighted title 10, tags 5, description 1 and category 2, then by the chosen sort.

## Settings

Settings are not in the database. They live in `Data\settings.json`: a versioned `AppSettings` document written
atomically (temp file plus replace) after a 500 ms debounce, and sanitised on load so a hand-edited file can't
push values out of range.

## Backups

A `.wfbackup` file is a zip containing a `VACUUM INTO` snapshot of the database (consistent while the app runs),
`settings.json`, the user themes and `backup.json` metadata. Restore is staged to `pending-restore.wfbackup` and
applied at the next start, before the database opens. Media isn't included; restored rows keep their original paths.
