using System.IO;
using System.Runtime.InteropServices;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Rendering;
using WallpaperFlow.Core.Updates;

namespace WallpaperFlow.App.ViewModels;

public sealed class AboutViewModel : PageViewModel
{
    public AboutViewModel(AppServices s, IUiDispatcher ui, IDialogService d) : base(s, ui, d)
    {
        CopyDiagnosticsCommand = new RelayCommand(() => { Dialogs.CopyText(Diagnostics); Say("Diagnostics copied to the clipboard."); });
        OpenLogsCommand = new RelayCommand(() => Dialogs.OpenFolder(AppPaths.Logs));
        OpenCreatorCommand = new RelayCommand(() => Dialogs.OpenUrl(AppInfo.CreatorUrl));
        OpenRepoCommand = new RelayCommand(() => Dialogs.OpenUrl(AppInfo.RepoUrl));
        ReportBugCommand = new RelayCommand(() => Dialogs.OpenUrl(AppInfo.IssuesUrl));
        var notices = Path.Combine(AppPaths.AppDir, "THIRD-PARTY-NOTICES.md");
        Notices = "WallpaperFlow Pro is free software, licensed under the GNU General Public License v3.0. "
            + "Source code: " + AppInfo.RepoUrl + "\n\n"
            + (File.Exists(notices) ? File.ReadAllText(notices) : "Third-party notices are installed with the app.");
    }

    public override string Title => "About";
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenCreatorCommand { get; }
    public RelayCommand OpenRepoCommand { get; }
    public RelayCommand ReportBugCommand { get; }
    public string CreatorName => AppInfo.CreatorName;
    public string Version => UpdateService.CurrentVersion.ToString(3);
    public string Notices { get; }

    public string Diagnostics
    {
        get
        {
            var gpus = string.Join("; ", Gpu.Adapters().Select(a => $"{a.Name} ({a.Vendor}, {a.DedicatedMb} MB)"));
            return string.Join(Environment.NewLine,
                $"WallpaperFlow Pro {Version} by {AppInfo.CreatorName}",
                $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
                $".NET: {RuntimeInformation.FrameworkDescription}",
                $"Displays: {string.Join("; ", S.Manager.Monitors.Select(m => $"{m.Label} {m.Bounds.Width}x{m.Bounds.Height}@{m.RefreshRate:0}Hz {m.Scale:P0}"))}",
                $"GPUs: {gpus}",
                $"Desktop mode: {(S.Desktop.RaisedDesktop ? "raised (Windows 11 24H2+)" : "WorkerW")}, ready: {S.Desktop.IsReady}",
                $"mpv: {S.Tools.Mpv ?? "missing"} · ffmpeg: {S.Tools.Ffmpeg ?? "missing"}",
                $"WebView2: {WebViewEnvironment.RuntimeVersion ?? "not installed"}",
                $"Library: {S.Library.Count()} wallpapers",
                $"Policy: {S.Policy.Current?.Summary}",
                $"Safe mode: {S.SafeMode}");
        }
    }
}
