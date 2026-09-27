using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Desktop;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Media;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Rendering;

namespace WallpaperFlow.Core.Engine;

/// <summary>Read-only view of a slot for the UI.</summary>
public sealed record SlotSnapshot(string Key, string MonitorLabel, IReadOnlyList<string> MonitorIds, RectI Bounds,
    string? WallpaperId, string Title, string? Thumbnail, PlaybackState State, double Fps, string Reason, WallpaperType? Type);

/// <summary>
/// Owns the running wallpapers. <see cref="ReconcileAsync"/> is idempotent: it compares the desired
/// layout (settings × connected monitors) with what is running and creates, moves, replaces or destroys
/// slots. All renderer and window work happens on the engine thread.
/// </summary>
public sealed class WallpaperManager : IAsyncDisposable
{
    private sealed class Slot
    {
        public required string Key;
        public required MonitorInfo[] Monitors;
        public RectI Bounds;
        public required WallpaperItem Wallpaper;
        public HostWindow? Host;
        public IWallpaperRenderer? Renderer;
        public SlotPolicy Policy = SlotPolicy.Playing;
        public readonly List<DateTime> Crashes = new();
        public bool Faulted;
        public string? FaultReason;
        public bool AudioOwner;
        public string? OptimizedSource;
    }

    public const string SpanKey = "span";
    private readonly EngineThread _engine;
    private readonly DesktopHost _desktop;
    private readonly SettingsStore _settings;
    private readonly LibraryRepository _library;
    private readonly ToolLocator _tools;
    private readonly VideoCacheService? _cache;
    private readonly Dictionary<string, Slot> _slots = new();
    private PolicyDecision _decision = new();
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private volatile SlotSnapshot[] _snapshot = Array.Empty<SlotSnapshot>();

    public event Action? SlotsChanged;
    public event Action<string, string>? WallpaperFaulted;

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
    public IReadOnlyList<SlotSnapshot> Snapshot => _snapshot;
    public bool NeedsAudio { get; private set; }
    public bool NeedsInput { get; private set; }
    public PolicyDecision Decision => _decision;

    public WallpaperManager(EngineThread engine, DesktopHost desktop, SettingsStore settings, LibraryRepository library,
        ToolLocator tools, VideoCacheService? cache)
    {
        _engine = engine;
        _desktop = desktop;
        _settings = settings;
        _library = library;
        _tools = tools;
        _cache = cache;
        if (_cache != null) _cache.Ready += id => _ = _engine.InvokeAsync(() => RestartWallpaperAsync(id));
    }

    public IReadOnlyList<int> ProcessIds
    {
        get
        {
            var ids = new HashSet<int>();
            foreach (var s in _snapshotSlots) foreach (var p in s) ids.Add(p);
            return ids.ToList();
        }
    }
    private volatile IReadOnlyList<int>[] _snapshotSlots = Array.Empty<IReadOnlyList<int>>();

    // ── public API (thread-safe; marshals to the engine thread) ───────────────

    public Task ReconcileAsync() => _engine.InvokeAsync(ReconcileCoreAsync);

    public Task RefreshMonitorsAsync() => _engine.InvokeAsync(async () =>
    {
        var now = DisplayService.Enumerate();
        if (DisplayService.Signature(now) == DisplayService.Signature(_monitors) && _slots.Count > 0) return;
        Log.Info($"Displays changed: {string.Join(", ", now.Select(m => $"{m.Label} {m.Bounds}"))}");
        _monitors = now;
        await ReconcileCoreAsync();
    });

    /// <summary>Explorer restarted: every host window died with it.</summary>
    public Task RecreateAllAsync() => _engine.InvokeAsync(async () =>
    {
        foreach (var s in _slots.Values.ToList()) await DestroySlotAsync(s);
        _slots.Clear();
        await Task.Delay(1500); // give Explorer time to build Progman/WorkerW again
        _desktop.Initialize();
        _monitors = DisplayService.Enumerate();
        await ReconcileCoreAsync();
    });

    public async Task SetWallpaperAsync(string wallpaperId, string? monitorId = null)
    {
        _settings.Update("displays", s =>
        {
            if (monitorId == null || s.Displays.Layout != LayoutMode.PerMonitor)
            {
                s.Displays.SharedWallpaperId = wallpaperId;
                if (s.Displays.Layout == LayoutMode.SameOnAll) s.Displays.PerMonitor.Clear();
            }
            else s.Displays.PerMonitor[monitorId] = wallpaperId;
        });
        try { _library.RecordUsage(wallpaperId); } catch (Exception ex) { Log.Warn("Usage not recorded: " + ex.Message); }
        await ReconcileAsync().ConfigureAwait(false);
    }

    public async Task ClearAsync()
    {
        _settings.Update("displays", s => { s.Displays.SharedWallpaperId = null; s.Displays.PerMonitor.Clear(); });
        await ReconcileAsync().ConfigureAwait(false);
    }

    public Task ApplyPolicyAsync(PolicyDecision decision) => _engine.InvokeAsync(async () =>
    {
        _decision = decision;
        foreach (var slot in _slots.Values.ToList()) await ApplySlotAsync(slot);
        Publish();
    });

    /// <summary>Settings affecting render options (volume, effects, quality…) changed.</summary>
    public Task RefreshOptionsAsync() => _engine.InvokeAsync(async () =>
    {
        AssignAudioOwner();
        foreach (var slot in _slots.Values)
        {
            var fresh = _library.Get(slot.Wallpaper.Id);
            if (fresh != null) slot.Wallpaper = fresh;
            if (slot.Renderer != null) await slot.Renderer.ApplyOptionsAsync(OptionsFor(slot));
        }
        Publish();
    });

    public Task RestartWallpaperAsync(string wallpaperId) => _engine.InvokeAsync(async () =>
    {
        foreach (var slot in _slots.Values.Where(s => s.Wallpaper.Id == wallpaperId).ToList())
        {
            await StopRendererAsync(slot);
            slot.Faulted = false;
            slot.Crashes.Clear();
            await ApplySlotAsync(slot);
        }
        Publish();
    });

    public void PostAudio(float[] left, float[] right, float level)
    {
        _engine.Post(() =>
        {
            foreach (var s in _slots.Values)
                if (s.Renderer is { WantsAudio: true } r) r.PostAudio(left, right, level);
        });
    }

    /// <summary>Screen coordinates (physical px) → the slot under the pointer, converted to host-relative px.</summary>
    public void RoutePointer(string kind, int screenX, int screenY, int button, int delta)
    {
        _engine.Post(() =>
        {
            foreach (var s in _slots.Values)
            {
                if (s.Renderer is not { WantsInput: true } r || !s.Bounds.Contains(screenX, screenY)) continue;
                r.PostPointer(new PointerInput(kind, screenX - s.Bounds.X, screenY - s.Bounds.Y, button, delta));
            }
        });
    }

    public void RouteKey(KeyInput key)
    {
        _engine.Post(() =>
        {
            foreach (var s in _slots.Values)
                if (s.Renderer is { WantsInput: true } r) r.PostKey(key);
        });
    }

    // ── engine-thread internals ─────────────────────────────────────────────

    private record Desired(string Key, MonitorInfo[] Monitors, RectI Bounds, WallpaperItem Wallpaper);

    private List<Desired> ComputeDesired()
    {
        var d = _settings.Current.Displays;
        var list = new List<Desired>();
        if (_monitors.Count == 0) _monitors = DisplayService.Enumerate();
        WallpaperItem? Lookup(string? id) => string.IsNullOrEmpty(id) ? null : _library.Get(id);

        switch (d.Layout)
        {
            case LayoutMode.Span:
                if (Lookup(d.SharedWallpaperId) is { } w)
                    list.Add(new Desired(SpanKey, _monitors.ToArray(), DisplayService.VirtualBounds(_monitors), w));
                break;
            case LayoutMode.PerMonitor:
                foreach (var m in _monitors)
                {
                    var id = d.PerMonitor.TryGetValue(m.Id, out var pid) ? pid : d.SharedWallpaperId;
                    if (Lookup(id) is { } pw) list.Add(new Desired(m.Id, new[] { m }, m.Bounds, pw));
                }
                break;
            default:
                if (Lookup(d.SharedWallpaperId) is { } sw)
                    foreach (var m in _monitors) list.Add(new Desired(m.Id, new[] { m }, m.Bounds, sw));
                break;
        }
        return list;
    }

    private async Task ReconcileCoreAsync()
    {
        if (!_desktop.IsReady && !_desktop.Initialize())
        {
            Log.Warn("Desktop not ready; reconcile postponed");
            return;
        }
        var desired = ComputeDesired();
        var keep = new HashSet<string>(desired.Select(x => x.Key));

        foreach (var stale in _slots.Values.Where(s => !keep.Contains(s.Key)).ToList())
        {
            await DestroySlotAsync(stale);
            _slots.Remove(stale.Key);
        }

        foreach (var want in desired)
        {
            if (_slots.TryGetValue(want.Key, out var slot))
            {
                if (slot.Wallpaper.Id != want.Wallpaper.Id)
                {
                    await StopRendererAsync(slot);
                    slot.Wallpaper = want.Wallpaper;
                    slot.Faulted = false;
                    slot.Crashes.Clear();
                    slot.OptimizedSource = null;
                }
                slot.Monitors = want.Monitors;
                if (slot.Bounds != want.Bounds)
                {
                    slot.Bounds = want.Bounds;
                    if (slot.Host != null)
                    {
                        _desktop.Place(slot.Host, slot.Bounds);
                        slot.Renderer?.Resize();
                    }
                }
            }
            else
            {
                slot = new Slot { Key = want.Key, Monitors = want.Monitors, Bounds = want.Bounds, Wallpaper = want.Wallpaper };
                _slots[want.Key] = slot;
            }
        }

        AssignAudioOwner();
        foreach (var slot in _slots.Values.ToList()) await ApplySlotAsync(slot);
        if (_slots.Count == 0) DesktopHost.RefreshStaticWallpaper();
        Publish();
    }

    private void AssignAudioOwner()
    {
        // Only one wallpaper may be audible: prefer the primary monitor.
        Slot? owner = null;
        if (!_settings.Current.General.Muted)
            owner = _slots.Values.Where(s => s.Wallpaper.Playback.Volume > 0)
                .OrderByDescending(s => s.Monitors.Any(m => m.IsPrimary)).FirstOrDefault();
        foreach (var s in _slots.Values) s.AudioOwner = ReferenceEquals(s, owner);
    }

    private SlotPolicy PolicyFor(Slot s) =>
        s.Monitors.Length == 1 ? _decision.For(s.Monitors[0].Id) : _decision.ForSpan(s.Monitors.Select(m => m.Id));

    private async Task ApplySlotAsync(Slot slot)
    {
        var policy = PolicyFor(slot);
        slot.Policy = policy;
        if (slot.Faulted) return;
        try
        {
            switch (policy.Target)
            {
                case SlotTarget.Stop:
                    await DestroySlotAsync(slot);
                    break;
                case SlotTarget.Pause:
                    if (slot.Renderer != null) await slot.Renderer.PauseAsync(policy.Deep);
                    break;
                default:
                    if (slot.Renderer == null) await StartRendererAsync(slot);
                    else
                    {
                        await slot.Renderer.ApplyOptionsAsync(OptionsFor(slot));
                        if (slot.Renderer.State == PlaybackState.Paused) await slot.Renderer.ResumeAsync();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Slot {slot.Key} failed to apply policy", ex);
            await HandleFaultAsync(slot, ex.Message);
        }
    }

    private RenderOptions OptionsFor(Slot slot)
    {
        var s = _settings.Current;
        var w = slot.Wallpaper;
        var monitor = slot.Monitors.FirstOrDefault(m => m.IsPrimary) ?? slot.Monitors.FirstOrDefault();
        var fps = _decision.FpsCap;
        if (w.Playback.FpsCap is > 0 and var own) fps = fps == 0 ? own : Math.Min(fps, own);
        return new RenderOptions
        {
            Scaling = w.Playback.Scaling,
            Volume = w.Playback.Volume,
            Muted = !slot.AudioOwner || s.General.Muted,
            Speed = w.Playback.Speed,
            Quality = _decision.ForceQuality ?? (s.Performance.Quality == QualityPreset.Auto ? QualityPreset.Balanced : s.Performance.Quality),
            HardwareDecoding = s.Performance.HardwareDecoding,
            D3D11Adapter = s.Performance.D3D11Adapter,
            FpsCap = fps,
            RenderScale = _decision.RenderScale,
            Effects = w.Effects,
            HeavyEffectsAllowed = _decision.HeavyEffectsAllowed,
            AllowNetwork = s.Security.AllowWebWallpaperNetwork || s.Security.NetworkAllowedWallpapers.Contains(w.Id),
            PropertiesJson = w.PropertiesJson,
            SlideDurationSeconds = w.Playback.SlideDurationSeconds,
            SlideShuffle = w.Playback.SlideShuffle,
            InputEnabled = s.Interaction.MouseEnabled || s.Interaction.KeyboardEnabled,
            MonitorScale = monitor?.Scale ?? 1,
            RefreshRate = monitor?.RefreshRate ?? 60,
            OptimizedSource = slot.OptimizedSource,
            SmartCache = s.Performance.SmartVideoCache,
        };
    }

    private async Task StartRendererAsync(Slot slot)
    {
        var w = slot.Wallpaper;
        if (w.Type == WallpaperType.Video && _cache != null && _settings.Current.Performance.SmartVideoCache)
        {
            // fpsCap 0: never lower a video's frame rate (that made 60 fps wallpapers look choppy).
            slot.OptimizedSource = _cache.TryGet(w, slot.Bounds.Width, slot.Bounds.Height, 0);
            if (slot.OptimizedSource == null) _cache.Request(w, slot.Bounds.Width, slot.Bounds.Height, 0);
        }

        slot.Host ??= _desktop.CreateHost(slot.Bounds, "WallpaperFlow " + slot.Key);
        var options = OptionsFor(slot);
        IWallpaperRenderer renderer = w.IsWeb
            ? new WebRenderer(w, slot.Host, options)
            : new MpvRenderer(_tools.Mpv ?? throw new InvalidOperationException("mpv.exe was not found. Run build\\fetch-deps.ps1 or set its path in Settings → Advanced."), w, slot.Host, options);
        renderer.Faulted += (r, reason) => _engine.Post(() => _ = HandleFaultAsync(slot, reason, r));
        slot.Renderer = renderer;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await renderer.StartAsync(cts.Token);
        if (slot.Policy.Target == SlotTarget.Pause) await renderer.PauseAsync(slot.Policy.Deep);
    }

    private async Task HandleFaultAsync(Slot slot, string reason, IWallpaperRenderer? source = null)
    {
        if (source != null && !ReferenceEquals(source, slot.Renderer)) return; // stale renderer
        var now = DateTime.UtcNow;
        slot.Crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(2));
        slot.Crashes.Add(now);
        await StopRendererAsync(slot);
        if (slot.Crashes.Count > 3)
        {
            slot.Faulted = true;
            slot.FaultReason = reason;
            Log.Error($"'{slot.Wallpaper.Title}' keeps failing; giving up: {reason}");
            WallpaperFaulted?.Invoke(slot.Wallpaper.Title, reason);
            Publish();
            return;
        }
        var delay = TimeSpan.FromSeconds(Math.Pow(2, slot.Crashes.Count - 1));
        Log.Warn($"Restarting '{slot.Wallpaper.Title}' in {delay.TotalSeconds:0}s after: {reason}");
        Publish();
        await Task.Delay(delay);
        if (_slots.TryGetValue(slot.Key, out var current) && ReferenceEquals(current, slot) && slot.Renderer == null && !slot.Faulted)
        {
            await ApplySlotAsync(slot);
            Publish();
        }
    }

    private static async Task StopRendererAsync(Slot slot)
    {
        var r = slot.Renderer;
        slot.Renderer = null;
        if (r != null)
        {
            try { await r.DisposeAsync(); } catch (Exception ex) { Log.Warn("Renderer dispose failed: " + ex.Message); }
        }
    }

    private async Task DestroySlotAsync(Slot slot)
    {
        await StopRendererAsync(slot);
        slot.Host?.Dispose();
        slot.Host = null;
        if (_slots.Values.All(s => s.Host == null)) DesktopHost.RefreshStaticWallpaper();
    }

    private void Publish()
    {
        _snapshot = _slots.Values.Select(s => new SlotSnapshot(
            s.Key,
            s.Key == SpanKey ? "All displays (span)" : s.Monitors.FirstOrDefault()?.Label ?? s.Key,
            s.Monitors.Select(m => m.Id).ToList(),
            s.Bounds,
            s.Wallpaper.Id,
            s.Wallpaper.Title,
            s.Wallpaper.Thumbnail,
            s.Faulted ? PlaybackState.Faulted : s.Renderer?.State ?? (s.Policy.Target == SlotTarget.Stop ? PlaybackState.Stopped : PlaybackState.Idle),
            s.Renderer?.CurrentFps ?? 0,
            s.Faulted ? s.FaultReason ?? "Failed" : s.Policy.Reason,
            s.Wallpaper.Type)).ToArray();
        _snapshotSlots = _slots.Values.Where(s => s.Renderer != null).Select(s => s.Renderer!.ProcessIds).ToArray();
        NeedsAudio = _slots.Values.Any(s => s.Renderer is { WantsAudio: true, State: PlaybackState.Playing });
        NeedsInput = _slots.Values.Any(s => s.Renderer is { WantsInput: true, State: PlaybackState.Playing });
        try { SlotsChanged?.Invoke(); } catch (Exception ex) { Log.Error("SlotsChanged handler failed", ex); }
    }

    /// <summary>Refreshes fps values in the snapshot (called by the metrics sampler).</summary>
    public Task RefreshSnapshotAsync() => _engine.InvokeAsync(Publish);

    public async ValueTask DisposeAsync()
    {
        await _engine.InvokeAsync(async () =>
        {
            foreach (var s in _slots.Values.ToList())
            {
                await StopRendererAsync(s);
                s.Host?.Dispose();
                s.Host = null;
            }
            _slots.Clear();
            DesktopHost.RefreshStaticWallpaper();
        }).ConfigureAwait(false);
    }
}
