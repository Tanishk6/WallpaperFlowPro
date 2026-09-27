using System.IO;
using System.Collections.ObjectModel;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.ViewModels;

public sealed class ThemeCardViewModel : ObservableObject
{
    private bool _current;
    public ThemeCardViewModel(ThemeDefinition t, bool current) { Theme = t; _current = current; }
    public ThemeDefinition Theme { get; }
    public string Name => Theme.Name;
    public string Accent => Theme.Accent;
    public string Highlight => Theme.Highlight;
    public string Surface => Theme.Mode == ThemeMode.Light ? "#EEF1F7" : "#0E1220";
    public string Ink => Theme.Mode == ThemeMode.Light ? "#141827" : "#E9ECF5";
    public string Description => Theme.Description;
    public string Meta => (Theme.BuiltIn ? "Built-in" : "Yours") + " · " + Theme.Mode + (Theme.Wallpapers?.Items.Count > 0 ? " · includes wallpapers" : "");
    public bool IsBuiltIn => Theme.BuiltIn;
    public bool IsCurrent { get => _current; set => SetProperty(ref _current, value); }
}

public sealed class ThemesViewModel : PageViewModel
{
    private readonly IThemeApplier _applier;
    private ThemeCardViewModel? _selected;
    private bool _applyWallpapers = true, _exportWallpapers = true;
    private string _editName = "", _editAccent = "", _editHighlight = "";
    private ThemeMode _editMode;

    public ThemesViewModel(AppServices s, IUiDispatcher ui, IDialogService d, IThemeApplier applier) : base(s, ui, d)
    {
        _applier = applier;
        ApplyCommand = new RelayCommand(p => Apply(p as ThemeCardViewModel ?? Selected));
        CaptureCommand = new RelayCommand(Capture);
        DuplicateCommand = new RelayCommand(p => Duplicate(p as ThemeCardViewModel ?? Selected));
        DeleteCommand = new RelayCommand(p => { if ((p as ThemeCardViewModel ?? Selected) is { IsBuiltIn: false } c && Dialogs.Confirm("Delete theme", $"Delete “{c.Name}”?")) { S.Themes.Delete(c.Theme.Id); Load(); } });
        ExportCommand = new RelayCommand(p => Export(p as ThemeCardViewModel ?? Selected));
        ImportCommand = new AsyncRelayCommand(ImportAsync, Fail);
        SaveEditCommand = new RelayCommand(SaveEdit, () => Selected is { IsBuiltIn: false });
        Load();
    }

    public override string Title => "Themes";
    public ObservableCollection<ThemeCardViewModel> Themes { get; } = new();
    public IReadOnlyList<ThemeMode> Modes { get; } = new[] { ThemeMode.Dark, ThemeMode.Light };
    public RelayCommand ApplyCommand { get; }
    public RelayCommand CaptureCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ExportCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand SaveEditCommand { get; }
    public bool ApplyWallpapers { get => _applyWallpapers; set => SetProperty(ref _applyWallpapers, value); }
    public bool ExportWallpapers { get => _exportWallpapers; set => SetProperty(ref _exportWallpapers, value); }

    public ThemeCardViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || value == null) return;
            EditName = value.Name; EditAccent = value.Accent; EditHighlight = value.Highlight; EditMode = value.Theme.Mode;
            OnPropertyChanged(nameof(CanEdit));
            SaveEditCommand.Refresh();
        }
    }
    public bool CanEdit => Selected is { IsBuiltIn: false };
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }
    public string EditAccent { get => _editAccent; set => SetProperty(ref _editAccent, value); }
    public string EditHighlight { get => _editHighlight; set => SetProperty(ref _editHighlight, value); }
    public ThemeMode EditMode { get => _editMode; set => SetProperty(ref _editMode, value); }

    private void Load()
    {
        var current = Cfg.Appearance.ThemeId;
        var keep = _selected?.Theme.Id;
        Themes.Clear();
        foreach (var t in S.Themes.All()) Themes.Add(new ThemeCardViewModel(t, t.Id == current));
        Selected = Themes.FirstOrDefault(t => t.Theme.Id == keep) ?? Themes.FirstOrDefault(t => t.IsCurrent);
    }

    private void Apply(ThemeCardViewModel? c)
    {
        if (c == null) return;
        S.Themes.Apply(c.Theme, ApplyWallpapers);
        _applier.Apply(c.Theme, c.Theme.Mode, c.Theme.Backdrop);
        foreach (var t in Themes) t.IsCurrent = t == c;
        Say($"“{c.Name}” applied.");
    }

    private void Capture()
    {
        var name = Dialogs.Prompt("Save current look as a theme", "Theme name", "My setup");
        if (string.IsNullOrWhiteSpace(name)) return;
        var t = S.Themes.CaptureCurrent(name);
        S.Themes.Save(t);
        Load();
        Selected = Themes.FirstOrDefault(x => x.Theme.Id == t.Id);
    }

    private void Duplicate(ThemeCardViewModel? c)
    {
        if (c == null) return;
        var copy = JsonDefaults.Deserialize<ThemeDefinition>(JsonDefaults.Serialize(c.Theme))!;
        copy.Id = "user." + Guid.NewGuid().ToString("N")[..10];
        copy.Name = c.Name + " copy";
        copy.BuiltIn = false;
        S.Themes.Save(copy);
        Load();
        Selected = Themes.FirstOrDefault(x => x.Theme.Id == copy.Id);
    }

    private void SaveEdit()
    {
        if (Selected is not { IsBuiltIn: false } c) return;
        c.Theme.Name = EditName.Trim().Length > 0 ? EditName.Trim() : c.Theme.Name;
        c.Theme.Accent = EditAccent.Trim();
        c.Theme.Highlight = EditHighlight.Trim();
        c.Theme.Mode = EditMode;
        S.Themes.Save(c.Theme);
        if (c.IsCurrent) _applier.Apply(c.Theme, c.Theme.Mode, c.Theme.Backdrop);
        Load();
        Say("Theme saved.");
    }

    private void Export(ThemeCardViewModel? c)
    {
        if (c == null) return;
        var path = Dialogs.SaveFile("Export theme", "WallpaperFlow theme|*.wftheme", AppPaths.SafeFileName(c.Name) + ".wftheme");
        if (path == null) return;
        try { S.Themes.Export(c.Theme, path, ExportWallpapers); Say("Theme exported. Share the .wftheme file with anyone using WallpaperFlow."); }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task ImportAsync()
    {
        var files = Dialogs.PickFiles("Import theme", "WallpaperFlow theme|*.wftheme", false);
        if (files is not { Length: 1 }) return;
        var (theme, media) = S.Themes.Import(files[0]);
        if (media != null)
        {
            var r = await S.Import.ImportPathsAsync(Directory.EnumerateFiles(media).ToArray());
            Say($"Imported “{theme.Name}” with {r.Added.Count} wallpaper(s).");
        }
        else Say($"Imported “{theme.Name}”.");
        Ui.Post(Load);
    }
}
