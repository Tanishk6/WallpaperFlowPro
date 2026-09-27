using System.IO;
using System.Collections.ObjectModel;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Engine;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Performance;
using WallpaperFlow.Core.Rendering;

namespace WallpaperFlow.App.ViewModels;

public sealed class ProcessRuleViewModel : ObservableObject
{
    private readonly Action _save;
    public ProcessRuleViewModel(ProcessRule r, Action save) { Rule = r; _save = save; }
    public ProcessRule Rule { get; }
    public string DisplayName => Rule.DisplayName;
    public string ProcessName => Rule.ProcessName;
    public PolicyAction Action { get => Rule.Action; set { Rule.Action = value; OnPropertyChanged(); _save(); } }
    public bool Enabled { get => Rule.Enabled; set { Rule.Enabled = value; OnPropertyChanged(); _save(); } }
}

public sealed class PerformanceViewModel : PageViewModel
{
    private PerfSample? _latest;
    private IReadOnlyList<double> _cpu = Array.Empty<double>(), _gpu = Array.Empty<double>(), _ram = Array.Empty<double>();

    public PerformanceViewModel(AppServices s, IUiDispatcher ui, IDialogService d) : base(s, ui, d)
    {
        FpsChoices = new[] { 0, 144, 120, 90, 60, 48, 30, 24, 15 }.Select(f => new Choice<int>(f, f == 0 ? "Match display" : $"{f} fps")).ToList();
        GpuPreferences = new[] { new Choice<int>(0, "Let Windows decide"), new Choice<int>(1, "Power saving (integrated GPU)"), new Choice<int>(2, "High performance (dedicated GPU)") };
        Adapters = new[] { new Choice<string?>(null, "Automatic") }.Concat(Gpu.Adapters().Where(a => !a.IsSoftware).Select(a => new Choice<string?>(a.Name, $"{a.Name} ({a.DedicatedMb / 1024.0:0.#} GB)"))).ToList();
        ClearCacheCommand = new RelayCommand(() => { S.VideoCache.Clear(); OnPropertyChanged(nameof(CacheSize)); Say("Optimised video cache cleared."); });
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand(p => { if (p is ProcessRuleViewModel r) { S.Settings.Update("detection", x => x.Detection.Rules.Remove(r.Rule)); LoadRules(); } });
        TrimCommand = new RelayCommand(() => { MemoryTrimmer.Trim(); Say("Memory trimmed."); });
        LoadRules();
    }

    public override string Title => "Performance";
    public IReadOnlyList<Choice<int>> FpsChoices { get; }
    public IReadOnlyList<Choice<int>> GpuPreferences { get; }
    public IReadOnlyList<Choice<string?>> Adapters { get; }
    public IReadOnlyList<QualityPreset> Qualities { get; } = Enum.GetValues<QualityPreset>();
    public IReadOnlyList<HardwareDecoding> Decoders { get; } = Enum.GetValues<HardwareDecoding>();
    public IReadOnlyList<PolicyAction> Actions { get; } = Enum.GetValues<PolicyAction>();
    public ObservableCollection<ProcessRuleViewModel> Rules { get; } = new();
    public ObservableCollection<SlotSnapshot> Slots { get; } = new();
    public RelayCommand ClearCacheCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public RelayCommand TrimCommand { get; }

    // ── live metrics ──
    public PerfSample? Latest { get => _latest; private set { SetProperty(ref _latest, value); OnPropertiesChanged(nameof(CpuText), nameof(GpuText), nameof(RamText), nameof(VramText), nameof(TempText), nameof(FpsText), nameof(PolicyText)); } }
    public IReadOnlyList<double> CpuHistory { get => _cpu; private set => SetProperty(ref _cpu, value); }
    public IReadOnlyList<double> GpuHistory { get => _gpu; private set => SetProperty(ref _gpu, value); }
    public IReadOnlyList<double> RamHistory { get => _ram; private set => SetProperty(ref _ram, value); }
    public string CpuText => _latest == null ? "—" : $"{_latest.AppCpu:0.0}%";
    public string GpuText => _latest == null ? "—" : $"{_latest.AppGpu:0.0}%";
    public string RamText => _latest == null ? "—" : $"{_latest.AppRamMb:0} MB";
    public string VramText => _latest == null ? "—" : $"{_latest.AppGpuMemMb:0} MB";
    public string TempText => _latest?.TemperatureC is { } t ? $"{t:0} °C" : "Not reported";
    public string FpsText => _latest == null ? "—" : $"{_latest.Fps:0}";
    public string SystemText => _latest == null ? "" : $"System: CPU {_latest.SystemCpu:0}% · GPU {_latest.SystemGpu:0}%";
    public string PolicyText => S.Policy.Current?.Summary ?? "Playing";
    public double RamMax => Math.Max(200, _ram.DefaultIfEmpty(0).Max() * 1.2);

    public override void OnActivated()
    {
        S.Metrics.SetFastMode(true);
        S.Metrics.Sampled += OnSample;
        var h = S.Metrics.History;
        Apply(h, S.Metrics.Latest);
    }

    public override void OnDeactivated()
    {
        S.Metrics.Sampled -= OnSample;
        S.Metrics.SetFastMode(false);
    }

    private void OnSample(PerfSample sample) { var h = S.Metrics.History; Ui.Post(() => Apply(h, sample)); }

    private void Apply(IReadOnlyList<PerfSample> history, PerfSample? latest)
    {
        CpuHistory = history.Select(x => x.AppCpu).ToList();
        GpuHistory = history.Select(x => x.AppGpu).ToList();
        RamHistory = history.Select(x => x.AppRamMb).ToList();
        Latest = latest;
        Slots.Clear();
        foreach (var sl in S.Manager.Snapshot) Slots.Add(sl);
        OnPropertiesChanged(nameof(SystemText), nameof(RamMax), nameof(CacheSize));
    }

    // ── rendering ──
    public Choice<int> FpsLimit { get => FpsChoices.FirstOrDefault(c => c.Value == Cfg.Performance.FpsLimit) ?? FpsChoices[0]; set => Update("performance", x => x.Performance.FpsLimit = value.Value); }
    public QualityPreset Quality { get => Cfg.Performance.Quality; set => Update("performance", x => x.Performance.Quality = value); }
    public bool DynamicFps { get => Cfg.Performance.DynamicFps; set => Update("performance", x => x.Performance.DynamicFps = value); }
    public int BackgroundFps { get => Cfg.Performance.BackgroundFps; set => Update("performance", x => x.Performance.BackgroundFps = Math.Clamp(value, 1, 60)); }
    public bool AdaptiveRendering { get => Cfg.Performance.AdaptiveRendering; set => Update("performance", x => x.Performance.AdaptiveRendering = value); }
    public int GpuBusyThreshold { get => Cfg.Performance.GpuBusyThreshold; set => Update("performance", x => x.Performance.GpuBusyThreshold = Math.Clamp(value, 20, 100)); }
    public double RenderScale { get => Cfg.Performance.RenderScale; set => Update("performance", x => x.Performance.RenderScale = Math.Clamp(Math.Round(value, 2), 0.25, 1)); }
    public bool SmartResolution { get => Cfg.Performance.SmartResolution; set => Update("performance", x => x.Performance.SmartResolution = value); }
    public HardwareDecoding HardwareDecoding { get => Cfg.Performance.HardwareDecoding; set => Update("performance", x => x.Performance.HardwareDecoding = value); }
    public bool SmartVideoCache { get => Cfg.Performance.SmartVideoCache; set => Update("performance", x => x.Performance.SmartVideoCache = value); }
    public int VideoCacheLimitGb { get => Cfg.Performance.VideoCacheLimitMb / 1024; set => Update("performance", x => x.Performance.VideoCacheLimitMb = Math.Clamp(value, 1, 200) * 1024); }
    public string CacheSize => Size(S.VideoCache.SizeBytes());
    public Choice<int> GpuPreference { get => GpuPreferences.FirstOrDefault(c => c.Value == Cfg.Performance.GpuPreference) ?? GpuPreferences[0]; set => Update("performance", x => x.Performance.GpuPreference = value.Value); }
    public Choice<string?> Adapter { get => Adapters.FirstOrDefault(a => a.Value == Cfg.Performance.D3D11Adapter) ?? Adapters[0]; set => Update("performance", x => x.Performance.D3D11Adapter = value.Value); }

    // ── detection ──
    public bool DetectionEnabled { get => Cfg.Detection.Enabled; set => Update("detection", x => x.Detection.Enabled = value); }
    public PolicyAction GamingAction { get => Cfg.Detection.GamingAction; set => Update("detection", x => x.Detection.GamingAction = value); }
    public PolicyAction FullscreenVideoAction { get => Cfg.Detection.FullscreenVideoAction; set => Update("detection", x => x.Detection.FullscreenVideoAction = value); }
    public PolicyAction PresentationAction { get => Cfg.Detection.PresentationAction; set => Update("detection", x => x.Detection.PresentationAction = value); }
    public bool PauseWhenCovered { get => Cfg.Detection.PauseWhenCovered; set => Update("detection", x => x.Detection.PauseWhenCovered = value); }
    public bool PauseOnLock { get => Cfg.Detection.PauseOnLock; set => Update("detection", x => x.Detection.PauseOnLock = value); }
    public int EscalateMinutes { get => Cfg.Detection.EscalateToStopAfterSeconds / 60; set => Update("detection", x => x.Detection.EscalateToStopAfterSeconds = Math.Max(0, value) * 60); }

    // ── battery ──
    public bool BatteryEnabled { get => Cfg.Battery.Enabled; set => Update("battery", x => x.Battery.Enabled = value); }
    public bool PauseWhenUnplugged { get => Cfg.Battery.PauseWhenUnplugged; set => Update("battery", x => x.Battery.PauseWhenUnplugged = value); }
    public int PauseBelowPercent { get => Cfg.Battery.PauseBelowPercent; set => Update("battery", x => x.Battery.PauseBelowPercent = Math.Clamp(value, 0, 100)); }
    public int BatteryFps { get => Cfg.Battery.ReducedFps; set => Update("battery", x => x.Battery.ReducedFps = Math.Clamp(value, 5, 60)); }
    public bool DisableHeavyEffects { get => Cfg.Battery.DisableHeavyEffects; set => Update("battery", x => x.Battery.DisableHeavyEffects = value); }
    public bool PauseOnEnergySaver { get => Cfg.Battery.PauseOnEnergySaver; set => Update("battery", x => x.Battery.PauseOnEnergySaver = value); }

    private void LoadRules()
    {
        Rules.Clear();
        foreach (var r in Cfg.Detection.Rules) Rules.Add(new ProcessRuleViewModel(r, () => S.Settings.Update("detection", _ => { })));
    }

    private void AddRule()
    {
        var exe = Dialogs.Prompt("Add app rule", "Process name (for example: obs64.exe or MyGame.exe)");
        if (string.IsNullOrWhiteSpace(exe)) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        S.Settings.Update("detection", x => x.Detection.Rules.Add(new ProcessRule
        {
            ProcessName = exe, DisplayName = Path.GetFileNameWithoutExtension(exe), Kind = RuleKind.Custom, Action = PolicyAction.Pause, Enabled = true,
        }));
        LoadRules();
    }
}
