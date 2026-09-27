using System.Collections.ObjectModel;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.ViewModels;

public sealed record InfoLine(string Label, string Value);

public sealed class EffectRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _enabled;
    private double _intensity;
    public EffectRowViewModel(EffectKind kind, bool enabled, double intensity, Action changed, bool isWeb)
    {
        Kind = kind; _enabled = enabled; _intensity = intensity; _changed = changed; IsWeb = isWeb;
    }
    public EffectKind Kind { get; }
    public bool IsWeb { get; }
    public string Label => Kind switch { EffectKind.ColorGrade => "Colour grading", EffectKind.MotionBlur => "Motion blur", _ => Kind.ToString() };
    public bool IsHeavy => EffectsProfile.IsHeavy(Kind);
    public string? Note => IsHeavy ? "Heavier on the GPU; switched off automatically on battery or when the system is busy." :
        IsWeb && Kind is EffectKind.Bloom or EffectKind.Glow ? "Approximated with CSS filters on web wallpapers." :
        IsWeb && Kind == EffectKind.MotionBlur ? "Video wallpapers only." : null;
    public bool Enabled { get => _enabled; set { if (SetProperty(ref _enabled, value)) _changed(); } }
    public double Intensity { get => _intensity; set { if (SetProperty(ref _intensity, Math.Round(value, 2))) { if (!_enabled && value > 0) { _enabled = true; OnPropertyChanged(nameof(Enabled)); } _changed(); } } }
}

/// <summary>Side panel for one wallpaper. Every change is saved and applied live (debounced 300 ms).</summary>
public sealed class WallpaperDetailViewModel : PageViewModel
{
    private readonly WallpaperItem _w;
    private CancellationTokenSource? _saveCts;
    private string _title, _tags, _category;
    private bool _restartNeeded;

    public WallpaperDetailViewModel(AppServices s, IUiDispatcher ui, IDialogService d, WallpaperItem item) : base(s, ui, d)
    {
        _w = item;
        _title = item.Title;
        _tags = string.Join(", ", item.Tags);
        _category = item.Category ?? "";
        foreach (var kind in Enum.GetValues<EffectKind>())
        {
            var e = item.Effects.Get(kind);
            Effects.Add(new EffectRowViewModel(kind, e?.Enabled ?? false, e?.Intensity ?? 0, ScheduleSave, item.IsWeb));
        }
        MonitorChoices = new[] { new Choice<string?>(null, "All displays") }
            .Concat(s.Manager.Monitors.Select(m => new Choice<string?>(m.Id, m.Label))).ToList();
        ApplyCommand = new AsyncRelayCommand(p => S.Manager.SetWallpaperAsync(_w.Id, (p as Choice<string?>)?.Value), Fail);
        ResetEffectsCommand = new RelayCommand(() => { foreach (var e in Effects) { e.Enabled = false; e.Intensity = 0; } });
    }

    public override string Title => _w.Title;
    public WallpaperItem Item => _w;
    public string? Thumbnail => _w.Thumbnail;
    public string? PreviewClip => _w.PreviewClip;
    public bool IsWeb => _w.IsWeb;
    public bool IsVideo => _w.Type == WallpaperType.Video;
    public bool IsSlideshow => _w.Type == WallpaperType.Slideshow;
    public bool HasAudio => _w.Type == WallpaperType.Video || _w.IsWeb;
    public ObservableCollection<EffectRowViewModel> Effects { get; } = new();
    public IReadOnlyList<Choice<string?>> MonitorChoices { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand ResetEffectsCommand { get; }
    public IReadOnlyList<ScalingMode> ScalingModes { get; } = Enum.GetValues<ScalingMode>();

    public string EditTitle { get => _title; set { if (SetProperty(ref _title, value)) { _w.Title = value.Trim().Length > 0 ? value.Trim() : _w.Title; ScheduleSave(); } } }
    public string Tags { get => _tags; set { if (SetProperty(ref _tags, value)) { _w.Tags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); ScheduleSave(); } } }
    public string Category { get => _category; set { if (SetProperty(ref _category, value)) { _w.Category = value.Trim().Length > 0 ? value.Trim() : null; ScheduleSave(); } } }

    public ScalingMode Scaling { get => _w.Playback.Scaling; set { _w.Playback = _w.Playback with { Scaling = value }; OnPropertyChanged(); ScheduleSave(); } }
    public int Volume { get => _w.Playback.Volume; set { _w.Playback = _w.Playback with { Volume = Math.Clamp(value, 0, 100) }; OnPropertyChanged(); ScheduleSave(); } }
    public double Speed { get => _w.Playback.Speed; set { _w.Playback = _w.Playback with { Speed = Math.Clamp(Math.Round(value, 2), 0.25, 4) }; OnPropertyChanged(); ScheduleSave(); } }
    public int FpsCap { get => _w.Playback.FpsCap ?? 0; set { _w.Playback = _w.Playback with { FpsCap = value <= 0 ? null : value }; OnPropertyChanged(); ScheduleSave(); } }
    public double SlideSeconds { get => _w.Playback.SlideDurationSeconds; set { _w.Playback = _w.Playback with { SlideDurationSeconds = Math.Clamp(value, 2, 3600) }; OnPropertyChanged(); _restartNeeded = true; ScheduleSave(); } }
    public bool SlideShuffle { get => _w.Playback.SlideShuffle; set { _w.Playback = _w.Playback with { SlideShuffle = value }; OnPropertyChanged(); _restartNeeded = true; ScheduleSave(); } }

    public bool AudioPulse { get => _w.Effects.AudioStyle.Pulse; set { _w.Effects.AudioStyle = _w.Effects.AudioStyle with { Pulse = value }; OnPropertyChanged(); ScheduleSave(); } }
    public bool AudioGlow { get => _w.Effects.AudioStyle.Glow; set { _w.Effects.AudioStyle = _w.Effects.AudioStyle with { Glow = value }; OnPropertyChanged(); ScheduleSave(); } }
    public bool AudioBurst { get => _w.Effects.AudioStyle.ParticleBurst; set { _w.Effects.AudioStyle = _w.Effects.AudioStyle with { ParticleBurst = value }; OnPropertyChanged(); ScheduleSave(); } }
    public bool AudioWaves { get => _w.Effects.AudioStyle.Waves; set { _w.Effects.AudioStyle = _w.Effects.AudioStyle with { Waves = value }; OnPropertyChanged(); ScheduleSave(); } }
    public double AudioSensitivity { get => _w.Effects.AudioStyle.Sensitivity; set { _w.Effects.AudioStyle = _w.Effects.AudioStyle with { Sensitivity = Math.Clamp(value, 0.2, 3) }; OnPropertyChanged(); ScheduleSave(); } }
    public bool BurstAndWavesSupported => _w.IsWeb;

    public bool AllowNetwork
    {
        get => Cfg.Security.AllowWebWallpaperNetwork || Cfg.Security.NetworkAllowedWallpapers.Contains(_w.Id);
        set
        {
            S.Settings.Update("security", x => { x.Security.NetworkAllowedWallpapers.Remove(_w.Id); if (value) x.Security.NetworkAllowedWallpapers.Add(_w.Id); });
            OnPropertyChanged();
            _ = S.Manager.RestartWallpaperAsync(_w.Id);
        }
    }
    public bool NetworkIsGlobal => Cfg.Security.AllowWebWallpaperNetwork;

    public IReadOnlyList<InfoLine> Info
    {
        get
        {
            var list = new List<InfoLine> { new("Type", _w.Type.ToString()) };
            if (_w.Width > 0) list.Add(new("Resolution", $"{_w.Width} × {_w.Height}"));
            if (_w.Fps > 0) list.Add(new("Frame rate", $"{_w.Fps:0.##} fps"));
            if (_w.Duration > 0) list.Add(new("Length", TimeSpan.FromSeconds(_w.Duration).ToString(@"m\:ss")));
            if (!string.IsNullOrEmpty(_w.Codec)) list.Add(new("Codec", _w.Codec!.ToUpperInvariant() + (_w.Bitrate > 0 ? $" · {_w.Bitrate / 1_000_000.0:0.#} Mbps" : "")));
            if (_w.FileSize > 0) list.Add(new("Size", Size(_w.FileSize)));
            list.Add(new("Added", _w.DateAdded.ToLocalTime().ToString("d MMM yyyy")));
            if (_w.UsageCount > 0) list.Add(new("Used", $"{_w.UsageCount} time{(_w.UsageCount == 1 ? "" : "s")}"));
            if (_w.QualityScore is { } q) list.Add(new("Quality", $"{q:0}/100" + (string.IsNullOrEmpty(_w.QualityFlags) ? "" : $" — {_w.QualityFlags}")));
            if (!string.IsNullOrEmpty(_w.Author)) list.Add(new("Author", _w.Author!));
            list.Add(new("Source", _w.Source));
            return list;
        }
    }

    private void ScheduleSave()
    {
        foreach (var row in Effects)
        {
            var e = _w.Effects.Get(row.Kind);
            if (e == null) { e = new EffectSetting { Kind = row.Kind }; _w.Effects.Items.Add(e); }
            e.Enabled = row.Enabled;
            e.Intensity = row.Intensity;
        }
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try
            {
                var restart = _restartNeeded;
                _restartNeeded = false;
                await S.SaveWallpaperAsync(_w, restart);
            }
            catch (Exception ex) { Fail(ex); }
        }, TaskScheduler.Default);
    }

    public override void Dispose() => _saveCts?.Cancel();
}
