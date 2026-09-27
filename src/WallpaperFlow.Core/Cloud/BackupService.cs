using System.IO.Compression;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Cloud;

/// <summary>
/// Backups contain the database (consistent VACUUM INTO snapshot), settings and themes. Media is not
/// included (it can be huge); restore re-links by path. Restores are staged and applied at next start
/// so we never swap the database under a live connection.
/// </summary>
public sealed class BackupService
{
    private readonly Database _db;
    private readonly SettingsStore _settings;
    public BackupService(Database db, SettingsStore settings) { _db = db; _settings = settings; }

    public string CreateBackup(string? destination = null)
    {
        Directory.CreateDirectory(AppPaths.Backups);
        destination ??= Path.Combine(AppPaths.Backups, $"wallpaperflow-{DateTime.Now:yyyyMMdd-HHmmss}.wfbackup");
        var dbSnap = Path.Combine(AppPaths.Temp, "backup-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Directory.CreateDirectory(AppPaths.Temp);
        _db.BackupTo(dbSnap);
        _settings.SaveNow();
        try
        {
            if (File.Exists(destination)) File.Delete(destination);
            using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(dbSnap, "library.db");
            if (File.Exists(AppPaths.SettingsFile)) zip.CreateEntryFromFile(AppPaths.SettingsFile, "settings.json");
            if (Directory.Exists(AppPaths.Themes))
                foreach (var t in Directory.EnumerateFiles(AppPaths.Themes, "*.json")) zip.CreateEntryFromFile(t, "themes/" + Path.GetFileName(t));
            using (var w = new StreamWriter(zip.CreateEntry("backup.json").Open()))
                w.Write(JsonDefaults.Serialize(new { version = 1, created = DateTime.UtcNow, app = typeof(BackupService).Assembly.GetName().Version?.ToString() }));
        }
        finally { try { File.Delete(dbSnap); } catch { } }
        _settings.Update("cloud", s => s.Cloud.LastBackup = DateTime.Now);
        Prune();
        Log.Info("Backup written: " + destination);
        return destination;
    }

    public void Prune()
    {
        var keep = Math.Max(1, _settings.Current.Cloud.BackupKeep);
        foreach (var f in new DirectoryInfo(AppPaths.Backups).EnumerateFiles("*.wfbackup").OrderByDescending(f => f.CreationTimeUtc).Skip(keep))
            try { f.Delete(); } catch { }
    }

    public IReadOnlyList<FileInfo> List() =>
        Directory.Exists(AppPaths.Backups) ? new DirectoryInfo(AppPaths.Backups).EnumerateFiles("*.wfbackup").OrderByDescending(f => f.CreationTimeUtc).ToList() : new List<FileInfo>();

    /// <summary>Validates and stages a backup; the app applies it on next launch.</summary>
    public void StageRestore(string backupFile)
    {
        using (var zip = ZipFile.OpenRead(backupFile))
            if (zip.GetEntry("library.db") == null) throw new InvalidDataException("Not a WallpaperFlow backup");
        File.Copy(backupFile, AppPaths.PendingRestore, true);
    }

    /// <summary>Called at startup before the database is opened.</summary>
    public static void ApplyPendingRestore()
    {
        if (!File.Exists(AppPaths.PendingRestore)) return;
        try
        {
            var temp = Path.Combine(AppPaths.Temp, "restore");
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Media.FileValidator.SafeExtract(AppPaths.PendingRestore, temp, 2L * 1024 * 1024 * 1024, 1000);
            foreach (var f in new[] { AppPaths.Database + "-wal", AppPaths.Database + "-shm" }) if (File.Exists(f)) File.Delete(f);
            File.Copy(Path.Combine(temp, "library.db"), AppPaths.Database, true);
            var settings = Path.Combine(temp, "settings.json");
            if (File.Exists(settings)) File.Copy(settings, AppPaths.SettingsFile, true);
            var themes = Path.Combine(temp, "themes");
            if (Directory.Exists(themes))
                foreach (var t in Directory.EnumerateFiles(themes)) File.Copy(t, Path.Combine(AppPaths.Themes, Path.GetFileName(t)), true);
            Log.Info("Backup restored");
        }
        catch (Exception ex) { Log.Error("Restoring backup failed", ex); }
        finally { try { File.Delete(AppPaths.PendingRestore); } catch { } }
    }
}
