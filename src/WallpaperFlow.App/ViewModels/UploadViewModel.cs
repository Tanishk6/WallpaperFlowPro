using System.IO;
using System.Collections.ObjectModel;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Import;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.App.ViewModels;

public sealed class WeProjectViewModel : ObservableObject
{
    private bool _selected;
    public WeProjectViewModel(WeProject p) { Project = p; _selected = p.Supported; }
    public WeProject Project { get; }
    public string Title => Project.Title;
    public string Kind => Project.Type switch { "video" => "Video", "web" => "Web", "scene" => "Scene", "application" => "Application", _ => Project.Type };
    public bool Supported => Project.Supported;
    public string? Reason => Project.Reason;
    public string? Preview => Project.Preview == null ? null : Path.Combine(Project.Folder, Project.Preview);
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value && Supported); }
}

public sealed class UploadViewModel : PageViewModel
{
    public const string FileFilter = "Wallpapers|*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.gif;*.apng;*.png;*.zip|Videos|*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv|Animated images|*.gif;*.apng;*.png|Packages|*.zip|All files|*.*";
    private readonly Action<string> _navigate;
    private string _url = "", _urlTitle = "", _shaderTitle = "My shader", _shaderCode;
    private ClockStyle _clockStyle = ClockStyle.Minimal;
    private bool _shaderAudio;
    private double _progress;
    private string _progressText = "";
    private bool _weScanned;

    public UploadViewModel(AppServices s, IUiDispatcher ui, IDialogService d, Action<string> navigate) : base(s, ui, d)
    {
        _navigate = navigate;
        _shaderCode = ReadRuntime("templates", "shader", "shader.frag") ?? "void mainImage(out vec4 c, in vec2 p) { c = vec4(p / iResolution.xy, 0.5, 1.0); }";
        PickFilesCommand = new AsyncRelayCommand(async () => { var f = Dialogs.PickFiles("Import wallpapers", FileFilter); if (f != null) await ImportAsync(f); }, Fail);
        PickFolderCommand = new AsyncRelayCommand(async () => { var f = Dialogs.PickFolder("Import a folder (images become a slideshow)"); if (f != null) await ImportAsync(new[] { f }); }, Fail);
        AddUrlCommand = new AsyncRelayCommand(AddUrlAsync, Fail, () => Url.Trim().Length > 0);
        CreateClockCommand = new AsyncRelayCommand(async () => Done(await S.Import.CreateClockAsync(ClockStyle, null)), Fail);
        CreateShaderCommand = new AsyncRelayCommand(async () => Done(await S.Import.CreateShaderAsync(ShaderCode, ShaderTitle, ShaderAudio)), Fail);
        LoadAuroraCommand = new RelayCommand(() => { if (ReadRuntime("samples", "shaders", "aurora.frag") is { } code) { ShaderCode = code; ShaderTitle = "Aurora"; ShaderAudio = true; } });
        ScanWeCommand = new AsyncRelayCommand(ScanWeAsync, Fail);
        ImportWeCommand = new AsyncRelayCommand(ImportWeAsync, Fail, () => WeProjects.Any(p => p.IsSelected));
        AddSamplesCommand = new AsyncRelayCommand(() => ImportAsync(new[] { "audio-visualizer", "interactive-particles" }
            .Select(n => Path.Combine(AppPaths.Runtime, "samples", n)).Where(Directory.Exists).ToArray()), Fail);
        OpenLibraryCommand = new RelayCommand(() => _navigate("library"));
    }

    public override string Title => "Upload";
    public ObservableCollection<string> Results { get; } = new();
    public ObservableCollection<WeProjectViewModel> WeProjects { get; } = new();
    public IReadOnlyList<ClockStyle> ClockStyles { get; } = Enum.GetValues<ClockStyle>();

    public AsyncRelayCommand PickFilesCommand { get; }
    public AsyncRelayCommand PickFolderCommand { get; }
    public AsyncRelayCommand AddUrlCommand { get; }
    public AsyncRelayCommand CreateClockCommand { get; }
    public AsyncRelayCommand CreateShaderCommand { get; }
    public RelayCommand LoadAuroraCommand { get; }
    public AsyncRelayCommand ScanWeCommand { get; }
    public AsyncRelayCommand ImportWeCommand { get; }
    public AsyncRelayCommand AddSamplesCommand { get; }
    public RelayCommand OpenLibraryCommand { get; }

    public string Url { get => _url; set { if (SetProperty(ref _url, value)) AddUrlCommand.Refresh(); } }
    public string UrlTitle { get => _urlTitle; set => SetProperty(ref _urlTitle, value); }
    public ClockStyle ClockStyle { get => _clockStyle; set => SetProperty(ref _clockStyle, value); }
    public string ShaderTitle { get => _shaderTitle; set => SetProperty(ref _shaderTitle, value); }
    public string ShaderCode { get => _shaderCode; set => SetProperty(ref _shaderCode, value); }
    public bool ShaderAudio { get => _shaderAudio; set => SetProperty(ref _shaderAudio, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public bool WeScanned { get => _weScanned; private set => SetProperty(ref _weScanned, value); }
    public bool HasResults => Results.Count > 0;

    public Task DropAsync(string[] paths) => ImportAsync(paths);

    private async Task ImportAsync(string[] paths)
    {
        if (paths.Length == 0) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<ImportProgress>(p =>
            {
                Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total * 100;
                ProgressText = p.Current.Length > 0 ? $"Importing {p.Current}…" : "";
            });
            var r = await Task.Run(() => S.Import.ImportPathsAsync(paths, progress));
            Report(r);
        }
        finally { IsBusy = false; ProgressText = ""; Progress = 0; }
    }

    private void Report(ImportResult r)
    {
        Ui.Post(() =>
        {
            foreach (var w in r.Added) Results.Insert(0, $"Added “{w.Title}”");
            foreach (var d in r.Duplicates) Results.Insert(0, $"Skipped duplicate: {d}");
            foreach (var e in r.Errors) Results.Insert(0, $"Couldn't import {e}");
            while (Results.Count > 100) Results.RemoveAt(Results.Count - 1);
            OnPropertyChanged(nameof(HasResults));
        });
        if (r.Errors.Count > 0 && r.Added.Count == 0) Fail(new InvalidOperationException(r.Summary)); else Say(r.Summary);
    }

    private void Done(Core.Models.WallpaperItem w)
    {
        Ui.Post(() => { Results.Insert(0, $"Created “{w.Title}”"); OnPropertyChanged(nameof(HasResults)); });
        Say($"“{w.Title}” is in your library.");
    }

    private async Task AddUrlAsync()
    {
        var w = await S.Import.ImportUrlAsync(Url.Trim(), UrlTitle);
        Done(w);
        Url = ""; UrlTitle = "";
    }

    private async Task ScanWeAsync()
    {
        var list = await Task.Run(WallpaperEngineImporter.Scan);
        Ui.Post(() =>
        {
            WeProjects.Clear();
            foreach (var p in list) WeProjects.Add(new WeProjectViewModel(p));
            WeScanned = true;
            ImportWeCommand.Refresh();
        });
        if (list.Count == 0) Say("No Wallpaper Engine projects found. Is Wallpaper Engine installed through Steam?");
    }

    private async Task ImportWeAsync()
    {
        var chosen = WeProjects.Where(p => p.IsSelected).ToList();
        IsBusy = true;
        try
        {
            var result = ImportResult.Empty();
            var importer = new WallpaperEngineImporter(S.Import);
            for (int i = 0; i < chosen.Count; i++)
            {
                ProgressText = $"Importing {chosen[i].Title}…";
                Progress = (double)i / chosen.Count * 100;
                await Task.Run(() => importer.ImportAsync(chosen[i].Project, result, CancellationToken.None));
            }
            Report(result);
        }
        finally { IsBusy = false; ProgressText = ""; Progress = 0; }
    }

    private static string? ReadRuntime(params string[] parts)
    {
        var p = Path.Combine(new[] { AppPaths.Runtime }.Concat(parts).ToArray());
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }
}
