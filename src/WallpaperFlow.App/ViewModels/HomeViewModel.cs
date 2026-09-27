using System.Collections.ObjectModel;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.ViewModels;

/// <summary>One monitor in the Home layout diagram, positioned in canvas units.</summary>
public sealed class MonitorTileViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Resolution { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool IsPrimary { get; init; }
    public string? Thumbnail { get; init; }
    public string Title { get; init; } = "No wallpaper";
    public string State { get; init; } = "";
    public bool IsPlaying { get; init; }
}

public sealed class HomeViewModel : PageViewModel
{
    public const double CanvasWidth = 560, CanvasHeight = 220;
    private readonly Action<string> _navigate;
    private int _libraryCount;
    private string _summary = "";

    public HomeViewModel(AppServices s, IUiDispatcher ui, IDialogService d, Action<string> navigate) : base(s, ui, d)
    {
        _navigate = navigate;
        ApplyCommand = new AsyncRelayCommand(p => p is WallpaperCardViewModel c ? S.Manager.SetWallpaperAsync(c.Id) : Task.CompletedTask, Fail);
        NextCommand = new AsyncRelayCommand(() => S.Scheduler.NextAsync(), Fail);
        OpenLibraryCommand = new RelayCommand(() => _navigate("library"));
        ImportCommand = new RelayCommand(() => _navigate("upload"));
        DisplaysCommand = new RelayCommand(() => _navigate("displays"));
    }

    public override string Title => "Home";
    public ObservableCollection<MonitorTileViewModel> Monitors { get; } = new();
    public ObservableCollection<WallpaperCardViewModel> Recommended { get; } = new();
    public ObservableCollection<WallpaperCardViewModel> Recent { get; } = new();
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public RelayCommand OpenLibraryCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand DisplaysCommand { get; }
    public int LibraryCount { get => _libraryCount; private set { SetProperty(ref _libraryCount, value); OnPropertyChanged(nameof(IsEmpty)); } }
    public bool IsEmpty => LibraryCount == 0;
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string? HeroImage => Monitors.FirstOrDefault(m => !string.IsNullOrEmpty(m.Thumbnail))?.Thumbnail;
    public string LayoutLabel => Cfg.Displays.Layout switch { LayoutMode.Span => "Spanned across displays", LayoutMode.PerMonitor => "Different on each display", _ => "Same on every display" };

    public override void OnActivated()
    {
        S.StateChanged += OnState;
        Refresh();
        _ = LoadListsAsync();
    }

    public override void OnDeactivated() => S.StateChanged -= OnState;
    private void OnState() => Ui.Post(Refresh);

    private void Refresh()
    {
        var monitors = S.Manager.Monitors;
        var snap = S.Manager.Snapshot;
        Monitors.Clear();
        if (monitors.Count > 0)
        {
            var v = monitors.Select(m => m.Bounds).Aggregate((a, b) => a.Union(b));
            double scale = Math.Min((CanvasWidth - 8) / v.Width, (CanvasHeight - 8) / v.Height);
            double ox = (CanvasWidth - v.Width * scale) / 2, oy = (CanvasHeight - v.Height * scale) / 2;
            foreach (var m in monitors)
            {
                var slot = snap.FirstOrDefault(x => x.MonitorIds.Contains(m.Id));
                Monitors.Add(new MonitorTileViewModel
                {
                    Id = m.Id, Label = m.Label, Resolution = $"{m.Bounds.Width}×{m.Bounds.Height} · {Math.Round(m.RefreshRate)} Hz",
                    X = ox + (m.Bounds.X - v.X) * scale + 3, Y = oy + (m.Bounds.Y - v.Y) * scale + 3,
                    Width = Math.Max(20, m.Bounds.Width * scale - 6), Height = Math.Max(20, m.Bounds.Height * scale - 6),
                    IsPrimary = m.IsPrimary, Thumbnail = slot?.Thumbnail, Title = slot?.Title ?? "No wallpaper",
                    State = slot == null ? "" : slot.State == PlaybackState.Playing ? (slot.Fps > 0 ? $"{slot.Fps:0} fps" : "Playing") : slot.Reason,
                    IsPlaying = slot?.State == PlaybackState.Playing,
                });
            }
        }
        // Tiles without a slot have an empty State; with no wallpaper at all, policy text would only confuse.
        Summary = Monitors.All(m => m.State.Length == 0) ? "No wallpaper set yet. Add one to get started." : S.Policy.Current?.Summary ?? "";
        OnPropertyChanged(nameof(LayoutLabel));
        OnPropertyChanged(nameof(HeroImage));
    }

    private async Task LoadListsAsync()
    {
        try
        {
            var (count, rec, recent) = await Task.Run(() =>
            {
                var c = S.Library.Count();
                var r = S.Ai.Recommendations(8);
                var rc = S.Library.Query(new LibraryQuery { Sort = SortField.LastUsed, Descending = true, Limit = 8 }).Where(w => w.LastUsed != null).ToList();
                return (c, r, rc);
            });
            var active = S.Manager.Snapshot.Select(x => x.WallpaperId).ToHashSet();
            Ui.Post(() =>
            {
                LibraryCount = count;
                Recommended.Clear();
                foreach (var w in rec) Recommended.Add(new WallpaperCardViewModel(w, S.Library, active.Contains(w.Id)));
                Recent.Clear();
                foreach (var w in recent) Recent.Add(new WallpaperCardViewModel(w, S.Library, active.Contains(w.Id)));
            });
        }
        catch (Exception ex) { Fail(ex); }
    }
}
