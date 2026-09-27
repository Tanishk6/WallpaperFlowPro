using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Engine;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Scheduling;

/// <summary>Rotates wallpapers hourly/daily/weekly/custom, sequential or random, plus time-of-day rules.</summary>
public sealed class Scheduler : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LibraryRepository _library;
    private readonly WallpaperManager _manager;
    private readonly Timer _timer;
    private readonly Random _random = new();
    private DateOnly _timeRulesDay = DateOnly.FromDateTime(DateTime.Now);
    private readonly HashSet<int> _firedRules = new();

    public Scheduler(SettingsStore settings, LibraryRepository library, WallpaperManager manager)
    {
        _settings = settings;
        _library = library;
        _manager = manager;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    /// <summary>Pure: is a rotation due at <paramref name="now"/>?</summary>
    public static bool IsDue(SchedulerSettings s, DateTime now)
    {
        if (!s.Enabled || s.Interval == ScheduleInterval.Off) return false;
        var last = s.LastRotation;
        switch (s.Interval)
        {
            case ScheduleInterval.Hourly: return last == null || now - last.Value >= TimeSpan.FromHours(1);
            case ScheduleInterval.Custom: return last == null || now - last.Value >= TimeSpan.FromMinutes(s.CustomMinutes);
            case ScheduleInterval.Daily:
            {
                var today = now.Date + s.DailyAt;
                var target = now >= today ? today : today.AddDays(-1);
                return last == null || last.Value < target;
            }
            case ScheduleInterval.Weekly:
            {
                int back = ((int)now.DayOfWeek - (int)s.WeeklyOn + 7) % 7;
                var target = now.Date.AddDays(-back) + s.DailyAt;
                if (target > now) target = target.AddDays(-7);
                return last == null || last.Value < target;
            }
        }
        return false;
    }

    /// <summary>Pure: choose the next wallpaper from candidates.</summary>
    public static WallpaperItem? PickNext(IReadOnlyList<WallpaperItem> candidates, ScheduleOrder order, string? currentId, Random random)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        if (order == ScheduleOrder.Sequential)
        {
            var sorted = candidates.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            var idx = sorted.FindIndex(c => c.Id == currentId);
            return sorted[(idx + 1) % sorted.Count];
        }
        var pool = candidates.Where(c => c.Id != currentId).ToList();
        return pool[random.Next(pool.Count)];
    }

    public IReadOnlyList<WallpaperItem> Candidates()
    {
        var s = _settings.Current.Scheduler;
        var q = new LibraryQuery { Limit = 5000 };
        switch (s.Source)
        {
            case ScheduleSource.Favorites: q = q with { FavoritesOnly = true }; break;
            case ScheduleSource.Collection: q = q with { CollectionId = s.SourceId }; break;
            case ScheduleSource.Folder: q = q with { FolderId = s.SourceId }; break;
        }
        var list = _library.Query(q);
        // Fall back to the whole library when favourites/collection is empty.
        return list.Count > 0 || s.Source == ScheduleSource.Library ? list : _library.Query(new LibraryQuery { Limit = 5000 });
    }

    public async Task NextAsync()
    {
        var s = _settings.Current;
        var candidates = Candidates();
        if (s.Displays.Layout == LayoutMode.PerMonitor && s.Scheduler.ApplyPerMonitor)
        {
            foreach (var m in _manager.Monitors)
            {
                var current = s.Displays.PerMonitor.GetValueOrDefault(m.Id);
                var next = PickNext(candidates, s.Scheduler.Order, current, _random);
                if (next != null) await _manager.SetWallpaperAsync(next.Id, m.Id).ConfigureAwait(false);
            }
        }
        else
        {
            var next = PickNext(candidates, s.Scheduler.Order, s.Displays.SharedWallpaperId, _random);
            if (next != null) await _manager.SetWallpaperAsync(next.Id).ConfigureAwait(false);
        }
        _settings.Update("scheduler", st => { st.Scheduler.LastRotation = DateTime.Now; st.Scheduler.LastWallpaperId = st.Displays.SharedWallpaperId; });
    }

    private async Task TickAsync()
    {
        try
        {
            var s = _settings.Current.Scheduler;
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            if (today != _timeRulesDay) { _timeRulesDay = today; _firedRules.Clear(); }

            for (int i = 0; i < s.TimeRules.Count; i++)
            {
                var rule = s.TimeRules[i];
                if (!rule.Enabled || _firedRules.Contains(i)) continue;
                if (now.TimeOfDay >= rule.At && now.TimeOfDay < rule.At + TimeSpan.FromMinutes(2))
                {
                    _firedRules.Add(i);
                    Log.Info($"Time rule {rule.At:hh\\:mm} → {rule.WallpaperId}");
                    await _manager.SetWallpaperAsync(rule.WallpaperId).ConfigureAwait(false);
                }
            }
            if (IsDue(s, now)) await NextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn("Scheduler tick failed: " + ex.Message); }
    }

    public void Dispose() => _timer.Dispose();
}
