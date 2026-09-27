using System.Collections.ObjectModel;
using System.Globalization;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.ViewModels;

public sealed class DisplayRowViewModel : ObservableObject
{
    private readonly Func<string?, Task> _assign;
    private Choice<string?>? _wallpaper;
    public DisplayRowViewModel(MonitorInfo m, IReadOnlyList<Choice<string?>> choices, string? assigned, Func<string?, Task> assign)
    {
        Monitor = m; Choices = choices; _assign = assign;
        _wallpaper = choices.FirstOrDefault(c => c.Value == assigned) ?? choices[0];
    }
    public MonitorInfo Monitor { get; }
    public string Label => Monitor.Label;
    public string Details => $"{Monitor.Bounds.Width}×{Monitor.Bounds.Height} · {Math.Round(Monitor.RefreshRate)} Hz · {Math.Round(Monitor.Scale * 100)}%" + (Monitor.IsPrimary ? " · Primary" : "");
    public IReadOnlyList<Choice<string?>> Choices { get; }
    public Choice<string?>? Wallpaper { get => _wallpaper; set { if (SetProperty(ref _wallpaper, value)) _ = _assign(value?.Value); } }
}

public sealed class TimeRuleViewModel : ObservableObject
{
    public TimeRuleViewModel(TimeRule rule, IReadOnlyList<Choice<string?>> choices) { Rule = rule; Choices = choices; }
    public TimeRule Rule { get; }
    public IReadOnlyList<Choice<string?>> Choices { get; }
    public string At { get => Rule.At.ToString(@"hh\:mm"); set { if (TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var t)) Rule.At = t; OnPropertyChanged(); } }
    public Choice<string?>? Wallpaper { get => Choices.FirstOrDefault(c => c.Value == Rule.WallpaperId); set { Rule.WallpaperId = value?.Value ?? ""; OnPropertyChanged(); } }
    public bool Enabled { get => Rule.Enabled; set { Rule.Enabled = value; OnPropertyChanged(); } }
}

public sealed class DisplaysViewModel : PageViewModel
{
    private IReadOnlyList<Choice<string?>> _choices = Array.Empty<Choice<string?>>();

    public DisplaysViewModel(AppServices s, IUiDispatcher ui, IDialogService d) : base(s, ui, d)
    {
        RefreshCommand = new AsyncRelayCommand(async () => { await S.Manager.RefreshMonitorsAsync(); Load(); }, Fail);
        NextNowCommand = new AsyncRelayCommand(() => S.Scheduler.NextAsync(), Fail);
        AddTimeRuleCommand = new RelayCommand(() => { S.Settings.Update("scheduler", x => x.Scheduler.TimeRules.Add(new TimeRule { At = new TimeSpan(20, 0, 0), WallpaperId = x.Displays.SharedWallpaperId ?? "", Enabled = true })); LoadRules(); });
        RemoveTimeRuleCommand = new RelayCommand(p => { if (p is TimeRuleViewModel r) { S.Settings.Update("scheduler", x => x.Scheduler.TimeRules.Remove(r.Rule)); LoadRules(); } });
        SaveRulesCommand = new RelayCommand(() => { S.Settings.Update("scheduler", _ => { }); Say("Time rules saved."); });
    }

    public override string Title => "Displays";
    public ObservableCollection<DisplayRowViewModel> Displays { get; } = new();
    public ObservableCollection<TimeRuleViewModel> TimeRules { get; } = new();
    public ObservableCollection<Choice<long?>> ScheduleSources { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextNowCommand { get; }
    public RelayCommand AddTimeRuleCommand { get; }
    public RelayCommand RemoveTimeRuleCommand { get; }
    public RelayCommand SaveRulesCommand { get; }
    public IReadOnlyList<Choice<string?>> WallpaperChoices => _choices;
    public IReadOnlyList<ScheduleInterval> Intervals { get; } = Enum.GetValues<ScheduleInterval>();
    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();

    public LayoutMode Layout { get => Cfg.Displays.Layout; set { Update("displays", x => x.Displays.Layout = value); Load(); } }
    public bool IsPerMonitor => Cfg.Displays.Layout == LayoutMode.PerMonitor;
    public Choice<string?>? SharedWallpaper
    {
        get => _choices.FirstOrDefault(c => c.Value == Cfg.Displays.SharedWallpaperId);
        set { if (value?.Value is { } id) _ = S.Manager.SetWallpaperAsync(id); else _ = S.Manager.ClearAsync(); OnPropertyChanged(); }
    }
    public bool RememberDisconnected { get => Cfg.Displays.RememberDisconnected; set => Update("displays", x => x.Displays.RememberDisconnected = value); }

    public bool SchedulerEnabled { get => Cfg.Scheduler.Enabled; set => Update("scheduler", x => x.Scheduler.Enabled = value); }
    public ScheduleInterval Interval { get => Cfg.Scheduler.Interval; set { Update("scheduler", x => x.Scheduler.Interval = value); OnPropertiesChanged(nameof(IsCustom), nameof(IsDailyOrWeekly), nameof(IsWeekly)); } }
    public bool IsCustom => Interval == ScheduleInterval.Custom;
    public bool IsDailyOrWeekly => Interval is ScheduleInterval.Daily or ScheduleInterval.Weekly;
    public bool IsWeekly => Interval == ScheduleInterval.Weekly;
    public int CustomMinutes { get => Cfg.Scheduler.CustomMinutes; set => Update("scheduler", x => x.Scheduler.CustomMinutes = Math.Clamp(value, 1, 10080)); }
    public bool RandomOrder { get => Cfg.Scheduler.Order == ScheduleOrder.Random; set => Update("scheduler", x => x.Scheduler.Order = value ? ScheduleOrder.Random : ScheduleOrder.Sequential); }
    public bool ApplyPerMonitor { get => Cfg.Scheduler.ApplyPerMonitor; set => Update("scheduler", x => x.Scheduler.ApplyPerMonitor = value); }
    public DayOfWeek WeeklyOn { get => Cfg.Scheduler.WeeklyOn; set => Update("scheduler", x => x.Scheduler.WeeklyOn = value); }
    public string DailyAt
    {
        get => Cfg.Scheduler.DailyAt.ToString(@"hh\:mm");
        set { if (TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var t)) Update("scheduler", x => x.Scheduler.DailyAt = t); }
    }
    public Choice<long?>? ScheduleSource
    {
        get => ScheduleSources.FirstOrDefault(c => SourceKey(c) == $"{Cfg.Scheduler.Source}:{Cfg.Scheduler.SourceId}");
        set
        {
            if (value == null) return;
            var (src, id) = Decode(value);
            Update("scheduler", x => { x.Scheduler.Source = src; x.Scheduler.SourceId = id; });
        }
    }
    private readonly Dictionary<Choice<long?>, ScheduleSource> _sourceKinds = new();
    private string SourceKey(Choice<long?> c) => $"{_sourceKinds.GetValueOrDefault(c)}:{c.Value}";
    private (ScheduleSource, long?) Decode(Choice<long?> c) => (_sourceKinds.GetValueOrDefault(c), c.Value);

    public override void OnActivated()
    {
        S.StateChanged += OnState;
        Load();
    }
    public override void OnDeactivated() => S.StateChanged -= OnState;
    private void OnState() { }

    private void Load()
    {
        _choices = new[] { new Choice<string?>(null, "None") }
            .Concat(S.Library.Query(new LibraryQuery { Sort = SortField.Title, Descending = false, Limit = 5000 }).Select(w => new Choice<string?>(w.Id, w.Title))).ToList();
        Displays.Clear();
        foreach (var m in S.Manager.Monitors)
        {
            var assigned = Cfg.Displays.PerMonitor.TryGetValue(m.Id, out var id) ? id : Cfg.Displays.SharedWallpaperId;
            var monitorId = m.Id;
            Displays.Add(new DisplayRowViewModel(m, _choices, assigned, wid => wid == null
                ? Task.Run(() => S.Settings.Update("displays", x => x.Displays.PerMonitor.Remove(monitorId)))
                : S.Manager.SetWallpaperAsync(wid, monitorId)));
        }
        ScheduleSources.Clear();
        _sourceKinds.Clear();
        void Add(ScheduleSource k, long? id, string label) { var c = new Choice<long?>(id, label); _sourceKinds[c] = k; ScheduleSources.Add(c); }
        Add(Core.Models.ScheduleSource.Library, null, "Whole library");
        Add(Core.Models.ScheduleSource.Favorites, null, "Favorites");
        foreach (var c in S.Library.Collections()) Add(Core.Models.ScheduleSource.Collection, c.Id, "Collection: " + c.Name);
        foreach (var f in S.Library.Folders()) Add(Core.Models.ScheduleSource.Folder, f.Id, "Folder: " + f.Name);
        LoadRules();
        OnPropertiesChanged(nameof(WallpaperChoices), nameof(SharedWallpaper), nameof(IsPerMonitor), nameof(Layout), nameof(ScheduleSource));
    }

    private void LoadRules()
    {
        TimeRules.Clear();
        foreach (var r in Cfg.Scheduler.TimeRules) TimeRules.Add(new TimeRuleViewModel(r, _choices));
    }
}
