using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Engine;

namespace WallpaperFlow.App.ViewModels;

public sealed record NavItem(string Key, string Title, string Glyph);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _s;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IThemeApplier _theme;
    private readonly Dictionary<string, PageViewModel> _pages = new();
    private PageViewModel? _current;
    private NavItem? _selected;
    private string _search = "";
    private string _status = "";
    private string? _nowPlaying;

    public IReadOnlyList<NavItem> Navigation { get; } = new[]
    {
        new NavItem("home", "Home", "\uE80F"),
        new NavItem("library", "Library", "\uE8F1"),
        new NavItem("upload", "Upload", "\uE898"),
        new NavItem("displays", "Displays", "\uE7F4"),
        new NavItem("performance", "Performance", "\uEC4A"),
        new NavItem("themes", "Themes", "\uE790"),
    };
    public IReadOnlyList<NavItem> FooterNavigation { get; } = new[]
    {
        new NavItem("settings", "Settings", "\uE713"),
        new NavItem("about", "About", "\uE946"),
    };

    public MainViewModel(AppServices services, IUiDispatcher ui, IDialogService dialogs, IThemeApplier theme)
    {
        _s = services;
        _ui = ui;
        _dialogs = dialogs;
        _theme = theme;
        NavigateCommand = new RelayCommand(p => Navigate(p as string ?? (p as NavItem)?.Key ?? "home"));
        TogglePauseCommand = new RelayCommand(_s.TogglePause);
        ToggleMuteCommand = new RelayCommand(_s.ToggleMute);
        NextCommand = new AsyncRelayCommand(() => _s.Scheduler.NextAsync());
        ExitSafeModeCommand = new AsyncRelayCommand(async () =>
        {
            _s.Settings.Update("security", x => x.Security.SafeMode = false);
            await _s.Manager.RefreshMonitorsAsync();
            OnPropertyChanged(nameof(IsSafeMode));
        });
        _s.StateChanged += OnState;
        _s.Policy.DecisionApplied += OnDecision;
        RefreshStatus();
        Navigate("home");
    }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand ExitSafeModeCommand { get; }

    public PageViewModel? CurrentPage { get => _current; private set => SetProperty(ref _current, value); }
    public NavItem? SelectedNav
    {
        get => _selected;
        set { if (value != null && SetProperty(ref _selected, value)) Navigate(value.Key); }
    }

    public string SearchText
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value)) return;
            var lib = (LibraryViewModel)Page("library");
            lib.Search = value;
            if (CurrentPage != lib && value.Length > 0) Navigate("library");
        }
    }

    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public string? NowPlaying { get => _nowPlaying; private set => SetProperty(ref _nowPlaying, value); }
    private string? _nowThumb;
    public string? NowPlayingThumbnail { get => _nowThumb; private set => SetProperty(ref _nowThumb, value); }
    public bool IsPaused => _s.Settings.Current.General.UserPaused;
    public bool IsMuted => _s.Settings.Current.General.Muted;
    public bool IsSafeMode => _s.SafeMode;
    public bool IsLive => !IsPaused && _s.Manager.Snapshot.Any(x => x.State == Core.Models.PlaybackState.Playing);

    public void Navigate(string key)
    {
        var page = Page(key);
        if (ReferenceEquals(page, CurrentPage)) return;
        CurrentPage?.OnDeactivated();
        CurrentPage = page;
        page.OnActivated();
        _selected = Navigation.Concat(FooterNavigation).FirstOrDefault(n => n.Key == key);
        OnPropertyChanged(nameof(SelectedNav));
    }

    public PageViewModel Page(string key)
    {
        if (_pages.TryGetValue(key, out var p)) return p;
        p = key switch
        {
            "library" => new LibraryViewModel(_s, _ui, _dialogs),
            "upload" => new UploadViewModel(_s, _ui, _dialogs, Navigate),
            "displays" => new DisplaysViewModel(_s, _ui, _dialogs),
            "performance" => new PerformanceViewModel(_s, _ui, _dialogs),
            "themes" => new ThemesViewModel(_s, _ui, _dialogs, _theme),
            "settings" => new SettingsViewModel(_s, _ui, _dialogs, _theme),
            "about" => new AboutViewModel(_s, _ui, _dialogs),
            _ => new HomeViewModel(_s, _ui, _dialogs, Navigate),
        };
        _pages[key] = p;
        return p;
    }

    private void OnState() => _ui.Post(RefreshStatus);
    private void OnDecision(PolicyDecision _) => _ui.Post(RefreshStatus);

    private void RefreshStatus()
    {
        var snap = _s.Manager.Snapshot;
        NowPlaying = snap.Count == 0 ? null : snap.Select(x => x.Title).Distinct().Count() == 1 ? snap[0].Title : $"{snap.Count} wallpapers";
        NowPlayingThumbnail = snap.FirstOrDefault(x => !string.IsNullOrEmpty(x.Thumbnail))?.Thumbnail;
        var summary = _s.Policy.Current?.Summary ?? "Playing";
        StatusText = snap.Count == 0 ? "No wallpaper" : snap.Any(x => x.State == Core.Models.PlaybackState.Faulted) ? "Needs attention" : summary;
        OnPropertiesChanged(nameof(IsPaused), nameof(IsMuted), nameof(IsLive), nameof(IsSafeMode));
    }

    public void Dispose()
    {
        _s.StateChanged -= OnState;
        _s.Policy.DecisionApplied -= OnDecision;
        CurrentPage?.OnDeactivated();
        foreach (var p in _pages.Values) p.Dispose();
        _pages.Clear();
    }
}
