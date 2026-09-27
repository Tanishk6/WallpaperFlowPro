using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Updates;

public sealed record UpdateCheck(UpdateManifest Manifest, bool IsNewer, bool IsMandatory);
public sealed record InstallerInfo(string Path, Version Version);

/// <summary>
/// Auto/manual updates with verification and rollback. An update is installed only if its SHA-256 matches
/// the manifest AND (by default) it carries a valid Authenticode signature from the expected publisher.
/// The installer keeps a copy of itself in %LocalAppData%\WallpaperFlow\installers, so rolling back simply
/// re-runs the previous version's (re-verified) installer.
/// </summary>
public sealed partial class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private readonly SettingsStore _settings;
    public UpdateService(SettingsStore settings) => _settings = settings;

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public async Task<UpdateCheck?> CheckAsync(CancellationToken ct = default)
    {
        var cfg = _settings.Current.Updates;
        string url;
        if (!string.IsNullOrWhiteSpace(cfg.ManifestUrl)) url = cfg.ManifestUrl.Replace("{channel}", cfg.Channel);
        else if (AppInfo.IsConfigured) url = AppInfo.UpdateManifestUrl;
        else return null;   // no repository configured yet: nothing to check
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Update manifest must use HTTPS");
        var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        var m = JsonDefaults.Deserialize<UpdateManifest>(json) ?? throw new InvalidDataException("Invalid update manifest");
        _settings.Update("updates", s => s.Updates.LastCheck = DateTime.Now);
        var newer = Version.TryParse(m.Version, out var v) && v > CurrentVersion;
        var mandatory = m.Mandatory || (m.MinimumVersion != null && Version.TryParse(m.MinimumVersion, out var min) && CurrentVersion < min);
        return new UpdateCheck(m, newer, mandatory);
    }

    public async Task<string> DownloadAsync(UpdateManifest m, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!m.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Insecure update URL");
        Directory.CreateDirectory(AppPaths.Updates);
        var path = Path.Combine(AppPaths.Updates, $"WallpaperFlowPro-Setup-{AppPaths.SafeFileName(m.Version, 20)}.exe");
        using var resp = await Http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[1 << 16];
            long read = 0; int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        Verify(path, m.Sha256);
        return path;
    }

    /// <summary>Throws if the installer fails hash or signature checks.</summary>
    public void Verify(string installer, string? expectedSha256)
    {
        if (!string.IsNullOrEmpty(expectedSha256))
        {
            using var fs = File.OpenRead(installer);
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update checksum mismatch");
        }
        var cfg = _settings.Current.Updates;
        if (cfg.RequireSignature)
        {
            if (!Authenticode.IsTrusted(installer)) throw new InvalidDataException("Update is not signed with a trusted certificate");
            var publisher = Authenticode.PublisherOf(installer) ?? "";
            if (!publisher.Contains(cfg.ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update is signed by '{publisher}', expected '{cfg.ExpectedPublisher}'");
        }
    }

    /// <summary>Starts the silent installer; the caller should exit the app right after.</summary>
    public static void RunInstaller(string installer)
    {
        Log.Info("Launching installer " + installer);
        Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS")
        {
            UseShellExecute = true,
        });
    }

    [GeneratedRegex(@"Setup-(\d+\.\d+\.\d+(?:\.\d+)?)\.exe$", RegexOptions.IgnoreCase)] private static partial Regex VersionRx();

    public static IReadOnlyList<InstallerInfo> RetainedInstallers()
    {
        if (!Directory.Exists(AppPaths.Installers)) return Array.Empty<InstallerInfo>();
        return Directory.EnumerateFiles(AppPaths.Installers, "*.exe")
            .Select(f => (f, m: VersionRx().Match(f)))
            .Where(x => x.m.Success && Version.TryParse(x.m.Groups[1].Value, out _))
            .Select(x => new InstallerInfo(x.f, Version.Parse(x.m.Groups[1].Value)))
            .OrderByDescending(i => i.Version).ToList();
    }

    public InstallerInfo? RollbackTarget() => RetainedInstallers().FirstOrDefault(i => i.Version < CurrentVersion);

    public void Rollback()
    {
        var target = RollbackTarget() ?? throw new InvalidOperationException("No previous version is available to roll back to");
        Verify(target.Path, null);
        RunInstaller(target.Path);
    }

    public static void PruneInstallers(int keep = 3)
    {
        foreach (var i in RetainedInstallers().Skip(keep)) try { File.Delete(i.Path); } catch { }
    }
}
