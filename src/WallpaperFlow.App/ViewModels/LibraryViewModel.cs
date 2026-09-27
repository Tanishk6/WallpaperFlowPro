using System.IO;
using System.Collections.ObjectModel;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.ViewModels;

public enum SourceKind { All, Favorites, Recent, Attention, Duplicates, Collection, Folder }
public sealed record LibrarySource(SourceKind Kind, long? Id, string Name, int? Count, string Glyph, string Section);

public sealed class LibraryViewModel : PageViewModel
{
    private CancellationTokenSource? _searchCts;
    private string _search = "";
    private LibrarySource? _source;
    private Choice<string> _typeFilter;
    private Choice<SortField> _sort;
    private bool _descending;
    private WallpaperCardViewModel? _selected;
    private WallpaperDetailViewModel? _detail;
    private int _resultCount;
    private bool _dirty = true;

    public LibraryViewModel(AppServices s, IUiDispatcher ui, IDialogService d) : base(s, ui, d)
    {
        TypeFilters = new[]
        {
            new Choice<string>("", "All types"), new Choice<string>("video", "Videos"), new Choice<string>("animated", "GIF & APNG"),
            new Choice<string>("web", "Web & URLs"), new Choice<string>("shader", "Shaders"), new Choice<string>("clock", "Clocks"), new Choice<string>("slideshow", "Slideshows"),
        };
        SortOptions = new[]
        {
            new Choice<SortField>(SortField.DateAdded, "Date added"), new Choice<SortField>(SortField.Title, "Name"), new Choice<SortField>(SortField.LastUsed, "Last used"),
            new Choice<SortField>(SortField.Usage, "Most used"), new Choice<SortField>(SortField.Resolution, "Resolution"), new Choice<SortField>(SortField.FileSize, "Size"),
            new Choice<SortField>(SortField.Duration, "Length"),
        };
        _typeFilter = TypeFilters[0];
        _sort = SortOptions.FirstOrDefault(o => o.Value == Cfg.Library.Sort) ?? SortOptions[0];
        _descending = Cfg.Library.SortDescending;

        ApplyCommand = new AsyncRelayCommand(p => (p as WallpaperCardViewModel ?? Selected) is { } c ? S.Manager.SetWallpaperAsync(c.Id) : Task.CompletedTask, Fail);
        FavoriteCommand = new RelayCommand(p => { if (p is WallpaperCardViewModel c) c.IsFavorite = !c.IsFavorite; });
        DeleteCommand = new RelayCommand(p => Delete(p as WallpaperCardViewModel ?? Selected));
        NewCollectionCommand = new RelayCommand(NewCollection);
        NewFolderCommand = new RelayCommand(NewFolder);
        AddToCollectionCommand = new RelayCommand(p => { if (p is LibrarySource { Kind: SourceKind.Collection, Id: { } id } && Selected != null) { S.Library.AddToCollection(id, Selected.Id); Say($"Added to {((LibrarySource)p!).Name}"); } });
        MoveToFolderCommand = new RelayCommand(p => { if (Selected != null) S.Library.MoveToFolder(Selected.Id, (p as LibrarySource)?.Id); });
        RemoveSourceCommand = new RelayCommand(p => RemoveSource(p as LibrarySource));
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeSelectedAsync, Fail);
        OpenLocationCommand = new RelayCommand(() => { if (Selected?.Item is { } w) Dialogs.OpenFolder(Directory.Exists(w.Source) ? w.Source : Path.GetDirectoryName(w.Source) ?? AppPaths.Library); });
        ToggleSortDirectionCommand = new RelayCommand(() => Descending = !Descending);
    }

    public override string Title => "Library";
    public ObservableCollection<WallpaperCardViewModel> Items { get; } = new();
    public ObservableCollection<LibrarySource> Sources { get; } = new();
    public IReadOnlyList<Choice<string>> TypeFilters { get; }
    public IReadOnlyList<Choice<SortField>> SortOptions { get; }
    public IEnumerable<LibrarySource> BuiltInSources => Sources.Where(x => x.Section == "Library").ToList();
    public IEnumerable<LibrarySource> Collections => Sources.Where(x => x.Kind == SourceKind.Collection).ToList();
    public IEnumerable<LibrarySource> Folders => Sources.Where(x => x.Kind == SourceKind.Folder).ToList();

    /// <summary>Cards grouped into rows so the grid can use a virtualizing list (thousands of wallpapers stay smooth).</summary>
    public ObservableCollection<IReadOnlyList<WallpaperCardViewModel>> Rows { get; } = new();
    private int _columns = 4;
    public int Columns { get => _columns; set { if (value > 0 && SetProperty(ref _columns, value)) BuildRows(); } }
    public RelayCommand SelectCommand => _select ??= new RelayCommand(p => Selected = p as WallpaperCardViewModel);
    private RelayCommand? _select;

    private void BuildRows()
    {
        Rows.Clear();
        for (int i = 0; i < Items.Count; i += _columns) Rows.Add(Items.Skip(i).Take(_columns).ToList());
    }

    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand FavoriteCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand NewCollectionCommand { get; }
    public RelayCommand NewFolderCommand { get; }
    public RelayCommand AddToCollectionCommand { get; }
    public RelayCommand MoveToFolderCommand { get; }
    public RelayCommand RemoveSourceCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand ToggleSortDirectionCommand { get; }

    public string Search { get => _search; set { if (SetProperty(ref _search, value)) Debounce(); } }
    public LibrarySource? Source { get => _source; set { if (value != null && SetProperty(ref _source, value)) _ = ReloadAsync(); } }
    public Choice<string> TypeFilter { get => _typeFilter; set { if (SetProperty(ref _typeFilter, value)) _ = ReloadAsync(); } }
    public Choice<SortField> Sort
    {
        get => _sort;
        set { if (SetProperty(ref _sort, value)) { S.Settings.Update("library", x => x.Library.Sort = value.Value); _ = ReloadAsync(); } }
    }
    public bool Descending
    {
        get => _descending;
        set { if (SetProperty(ref _descending, value)) { S.Settings.Update("library", x => x.Library.SortDescending = value); _ = ReloadAsync(); } }
    }
    public int ResultCount { get => _resultCount; private set { SetProperty(ref _resultCount, value); OnPropertyChanged(nameof(IsEmpty)); } }
    public bool IsEmpty => ResultCount == 0 && !IsBusy;
    public string EmptyText => string.IsNullOrWhiteSpace(Search) ? "Nothing here yet. Drop videos, GIFs or web wallpapers on the Upload page." : $"No wallpapers match “{Search}”.";

    public WallpaperCardViewModel? Selected
    {
        get => _selected;
        set
        {
            if (_selected != null) _selected.IsSelected = false;
            if (!SetProperty(ref _selected, value)) return;
            if (value != null) value.IsSelected = true;
            Detail?.Dispose();
            Detail = value == null ? null : new WallpaperDetailViewModel(S, Ui, Dialogs, value.Item);
        }
    }
    public WallpaperDetailViewModel? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public override void OnActivated()
    {
        S.Library.Changed += OnLibraryChanged;
        S.StateChanged += OnActiveChanged;
        if (_dirty) { LoadSources(); _ = ReloadAsync(); }
    }

    public override void OnDeactivated()
    {
        S.Library.Changed -= OnLibraryChanged;
        S.StateChanged -= OnActiveChanged;
    }

    private void OnLibraryChanged() => Debounce(400, sources: true);

    private void OnActiveChanged() => Ui.Post(() =>
    {
        var active = S.Manager.Snapshot.Select(x => x.WallpaperId).ToHashSet();
        foreach (var c in Items) c.IsActive = active.Contains(c.Id);
    });

    private void Debounce(int ms = 160, bool sources = false)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _ = Task.Delay(ms, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (sources) Ui.Post(LoadSources);
            _ = ReloadAsync();
        }, TaskScheduler.Default);
    }

    private void LoadSources()
    {
        var keep = _source;
        Sources.Clear();
        Sources.Add(new LibrarySource(SourceKind.All, null, "All wallpapers", S.Library.Count(), "\uE8F1", "Library"));
        Sources.Add(new LibrarySource(SourceKind.Favorites, null, "Favorites", null, "\uE734", "Library"));
        Sources.Add(new LibrarySource(SourceKind.Recent, null, "Recently used", null, "\uE823", "Library"));
        Sources.Add(new LibrarySource(SourceKind.Attention, null, "Low quality", null, "\uE7BA", "Library"));
        Sources.Add(new LibrarySource(SourceKind.Duplicates, null, "Duplicates", null, "\uE8C8", "Library"));
        foreach (var c in S.Library.Collections()) Sources.Add(new LibrarySource(SourceKind.Collection, c.Id, c.Name, c.Count, "\uE8B7", "Collections"));
        foreach (var f in S.Library.Folders()) Sources.Add(new LibrarySource(SourceKind.Folder, f.Id, f.Name, f.Count, "\uE8B7", "Folders"));
        _source = Sources.FirstOrDefault(x => x.Kind == keep?.Kind && x.Id == keep?.Id) ?? Sources[0];
        OnPropertiesChanged(nameof(Source), nameof(BuiltInSources), nameof(Collections), nameof(Folders));
        _dirty = false;
    }

    public async Task ReloadAsync()
    {
        var source = _source ?? new LibrarySource(SourceKind.All, null, "", null, "", "");
        var type = _typeFilter.Value;
        var q = new LibraryQuery
        {
            Search = _search, Sort = _sort.Value, Descending = _descending, Limit = 2000,
            FavoritesOnly = source.Kind == SourceKind.Favorites,
            CollectionId = source.Kind == SourceKind.Collection ? source.Id : null,
            FolderId = source.Kind == SourceKind.Folder ? source.Id : null,
            Type = type switch { "video" => WallpaperType.Video, "shader" => WallpaperType.Shader, "clock" => WallpaperType.Clock, "slideshow" => WallpaperType.Slideshow, _ => null },
        };
        if (source.Kind == SourceKind.Recent) q = q with { Sort = SortField.LastUsed, Descending = true, Limit = 60 };
        try
        {
            IsBusy = true;
            var list = await Task.Run(() =>
            {
                IEnumerable<WallpaperItem> items = source.Kind switch
                {
                    SourceKind.Attention => S.Ai.LowQuality(),
                    SourceKind.Duplicates => S.Ai.FindDuplicates().SelectMany(g => g.Items),
                    _ => S.Library.Query(q),
                };
                if (source.Kind == SourceKind.Recent) items = items.Where(w => w.LastUsed != null);
                if (type == "animated") items = items.Where(w => w.Type is WallpaperType.Gif or WallpaperType.Apng);
                if (type == "web") items = items.Where(w => w.Type is WallpaperType.Html or WallpaperType.Url);
                return items.ToList();
            });
            var active = S.Manager.Snapshot.Select(x => x.WallpaperId).ToHashSet();
            await Ui.InvokeAsync(() =>
            {
                var selectedId = Selected?.Id;
                Items.Clear();
                foreach (var w in list) Items.Add(new WallpaperCardViewModel(w, S.Library, active.Contains(w.Id)));
                ResultCount = Items.Count;
                BuildRows();
                if (selectedId != null)
                {
                    var again = Items.FirstOrDefault(i => i.Id == selectedId);
                    if (again == null) Selected = null;
                    else { _selected = again; again.IsSelected = true; OnPropertyChanged(nameof(Selected)); }
                }
                OnPropertyChanged(nameof(EmptyText));
            });
        }
        catch (Exception ex) { Fail(ex); }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    private void Delete(WallpaperCardViewModel? card)
    {
        if (card == null) return;
        var w = card.Item;
        bool owned = AppPaths.IsInside(AppPaths.Library, w.Source);
        var msg = owned ? $"Remove “{w.Title}” from your library and delete its copy in the library folder?" : $"Remove “{w.Title}” from your library? The original file stays where it is.";
        if (!Dialogs.Confirm("Remove wallpaper", msg, "Remove")) return;
        if (S.Manager.Snapshot.Any(x => x.WallpaperId == w.Id))
        {
            if (Cfg.Displays.SharedWallpaperId == w.Id) S.Settings.Update("displays", x => x.Displays.SharedWallpaperId = null);
            S.Settings.Update("displays", x => { foreach (var k in x.Displays.PerMonitor.Where(kv => kv.Value == w.Id).Select(kv => kv.Key).ToList()) x.Displays.PerMonitor.Remove(k); });
        }
        S.Library.Delete(w.Id);
        try
        {
            if (owned)
            {
                var dir = Directory.Exists(w.Source) ? w.Source : Path.GetDirectoryName(w.Source);
                if (dir != null && AppPaths.IsInside(AppPaths.Library, dir) && !string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), Path.GetFullPath(AppPaths.Library).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(dir, true);
            }
            foreach (var f in new[] { w.Thumbnail, w.PreviewClip }) if (f != null && File.Exists(f)) File.Delete(f);
        }
        catch (Exception ex) { Log.Warn("Cleanup after delete failed: " + ex.Message); }
        Items.Remove(card);
        ResultCount = Items.Count;
        BuildRows();
        if (Selected == card) Selected = null;
    }

    private void NewCollection()
    {
        var name = Dialogs.Prompt("New collection", "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = S.Library.CreateCollection(name);
        if (Selected != null) S.Library.AddToCollection(id, Selected.Id);
    }

    private void NewFolder()
    {
        var name = Dialogs.Prompt("New folder", "Name");
        if (!string.IsNullOrWhiteSpace(name)) S.Library.CreateFolder(name);
    }

    private void RemoveSource(LibrarySource? src)
    {
        if (src is not { Id: { } id }) return;
        if (!Dialogs.Confirm("Delete " + (src.Kind == SourceKind.Collection ? "collection" : "folder"), $"Delete “{src.Name}”? Wallpapers in it stay in your library.")) return;
        if (src.Kind == SourceKind.Collection) S.Library.DeleteCollection(id); else S.Library.DeleteFolder(id);
    }

    private async Task AnalyzeSelectedAsync()
    {
        if (Selected == null) return;
        await S.Ai.AnalyzeAsync(Selected.Item, allowCloud: true);
        if (S.Library.Get(Selected.Id) is { } fresh) { Selected.Refresh(fresh); Detail = new WallpaperDetailViewModel(S, Ui, Dialogs, fresh); }
        Say("Tags, category and quality updated.");
    }

    public void ImportDropped(string[] paths) => _ = Task.Run(async () =>
    {
        try
        {
            IsBusy = true;
            var r = await S.Import.ImportPathsAsync(paths);
            Say(r.Summary);
        }
        catch (Exception ex) { Fail(ex); }
        finally { IsBusy = false; }
    });
}
