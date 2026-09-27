using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WallpaperFlow.Core.Desktop;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Rendering;

/// <summary>
/// Video / GIF / APNG / image-slideshow renderer backed by an mpv child process that draws straight
/// into our host window (--wid) through Direct3D 11 (gpu-next + d3d11 + D3D11VA/NVDEC decoding).
/// Decoding and presentation stay entirely on the GPU; the app process only exchanges small JSON
/// messages. A crashed or hung mpv never takes the app (or Explorer) down with it.
/// </summary>
public sealed class MpvRenderer : IWallpaperRenderer
{
    private readonly string _mpvPath;
    private readonly string _pipeName = "wallpaperflow-mpv-" + Guid.NewGuid().ToString("N")[..12];
    private Process? _process;
    private MpvIpcClient? _ipc;
    private MpvEffectPlan _effects = MpvEffectPlan.Empty;
    private string? _playlistFile;
    private bool _disposed;
    private double _zoomBase;

    public WallpaperItem Wallpaper { get; }
    public HostWindow Host { get; }
    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public RenderOptions Options { get; private set; }
    public double CurrentFps { get; private set; }
    public bool WantsAudio => Wallpaper.IsAudioReactive || Options.Effects.AudioStyle.AnyEnabled;
    public bool WantsInput => false;
    public IReadOnlyList<int> ProcessIds => _process is { HasExited: false } p ? new[] { p.Id } : Array.Empty<int>();
    public event Action<IWallpaperRenderer, string>? Faulted;

    public MpvRenderer(string mpvPath, WallpaperItem wallpaper, HostWindow host, RenderOptions options)
    {
        _mpvPath = mpvPath;
        Wallpaper = wallpaper;
        Host = host;
        Options = options;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        State = PlaybackState.Loading;
        _effects = EffectsCompiler.ForMpv(Options.Effects, Options.HeavyEffectsAllowed);
        var args = BuildArguments(Options);

        var psi = new ProcessStartInfo(_mpvPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnExited;
        if (!_process.Start()) throw new InvalidOperationException("mpv failed to start");
        ChildProcessJob.Assign(_process);

        _ipc = new MpvIpcClient(_pipeName);
        await _ipc.ConnectAsync(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        _ipc.PropertyChanged += OnProperty;
        _ipc.EventReceived += OnEvent;
        await _ipc.ObserveAsync(1, "estimated-vf-fps").ConfigureAwait(false);
        await _ipc.ObserveAsync(2, "pause").ConfigureAwait(false);
        if (_effects.Shaders.Count > 0) await ApplyEffectsAsync().ConfigureAwait(false);
        State = PlaybackState.Playing;
        Log.Info($"mpv started for '{Wallpaper.Title}' (pid {_process.Id}) on {Host.ScreenBounds}");
    }

    internal List<string> BuildArguments(RenderOptions o)
    {
        var source = o.OptimizedSource ?? Wallpaper.ResolveEntryPath();
        var a = new List<string>
        {
            "--no-config",
            "--wid=" + ((long)Host.Handle).ToString(CultureInfo.InvariantCulture),
            "--input-ipc-server=\\\\.\\pipe\\" + _pipeName,
            "--idle=no", "--force-window=yes", "--keep-open=no",
            "--terminal=no", "--msg-level=all=error",
            "--osc=no", "--osd-level=0", "--input-default-bindings=no", "--input-vo-keyboard=no",
            "--input-cursor=no", "--cursor-autohide=always", "--ytdl=no", "--load-scripts=no", "--load-stats-overlay=no",
            "--sub-auto=no", "--audio-display=no", "--hr-seek=no",
            "--vo=gpu-next", "--gpu-api=d3d11", "--d3d11-flip=yes",
            "--hwdec=" + HwDec(o.HardwareDecoding, _effects.NeedsCopyBack),
            "--vd-lavc-threads=2",
            "--speed=" + o.Speed.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(o.D3D11Adapter)) a.Add("--d3d11-adapter=" + o.D3D11Adapter);

        // Audio: never decode it unless the user actually wants to hear this wallpaper.
        if (o.Muted || o.Volume <= 0) a.Add("--aid=no");
        else a.Add("--volume=" + o.Volume.ToString(CultureInfo.InvariantCulture));

        a.AddRange(ScalingArgs(o.Scaling));
        a.AddRange(QualityArgs(o.Quality));

        // Smart cache: small loops live entirely in RAM (no disk wake-ups each loop); large files stream.
        long size = Wallpaper.FileSize;
        if (o.SmartCache && size > 0 && size <= 48L * 1024 * 1024 && Wallpaper.Type == WallpaperType.Video)
            a.AddRange(new[] { "--cache=yes", "--demuxer-max-bytes=64MiB", "--demuxer-max-back-bytes=64MiB", "--cache-secs=3600" });
        else
            a.AddRange(new[] { "--cache=no", "--demuxer-max-bytes=16MiB", "--demuxer-max-back-bytes=4MiB" });

        if (_effects.VideoFilter != null) a.Add("--vf=" + _effects.VideoFilter);

        if (Wallpaper.Type == WallpaperType.Slideshow)
        {
            _playlistFile = BuildPlaylist();
            a.Add("--image-display-duration=" + o.SlideDurationSeconds.ToString("0.#", CultureInfo.InvariantCulture));
            a.Add("--loop-playlist=inf");
            if (o.SlideShuffle) a.Add("--shuffle");
            a.Add("--playlist=" + _playlistFile);
        }
        else
        {
            a.Add("--loop-file=inf");
            if (Wallpaper.Type is WallpaperType.Gif or WallpaperType.Apng) a.Add("--image-display-duration=inf");
            a.Add("--");
            a.Add(source);
        }
        return a;
    }

    internal static string HwDec(HardwareDecoding h, bool copyBack) => (h, copyBack) switch
    {
        (HardwareDecoding.Off, _) => "no",
        (_, true) => "auto-copy-safe",
        (HardwareDecoding.D3D11, _) => "d3d11va",
        (HardwareDecoding.Nvdec, _) => "nvdec",
        _ => "auto-safe",
    };

    internal static IEnumerable<string> ScalingArgs(ScalingMode s) => s switch
    {
        ScalingMode.Fill => new[] { "--panscan=1.0", "--keepaspect=yes", "--video-unscaled=no" },
        ScalingMode.Stretch => new[] { "--panscan=0", "--keepaspect=no", "--video-unscaled=no" },
        ScalingMode.Center => new[] { "--panscan=0", "--keepaspect=yes", "--video-unscaled=yes" },
        _ => new[] { "--panscan=0", "--keepaspect=yes", "--video-unscaled=no" },
    };

    internal static IReadOnlyList<(string Prop, string Value)> QualityProps(QualityPreset q) => q switch
    {
        QualityPreset.Performance => new[]
        {
            ("scale", "catmull_rom"), ("dscale", "bilinear"), ("cscale", "bilinear"), ("deband", "no"),
            ("dither-depth", "auto"), ("correct-downscaling", "no"), ("linear-downscaling", "no"), ("sigmoid-upscaling", "no"),
        },
        QualityPreset.Quality => new[]
        {
            ("scale", "ewa_lanczossharp"), ("dscale", "mitchell"), ("cscale", "ewa_lanczossharp"), ("deband", "yes"),
            ("dither-depth", "auto"), ("correct-downscaling", "yes"), ("linear-downscaling", "yes"), ("sigmoid-upscaling", "yes"),
        },
        _ => new[]
        {
            ("scale", "spline36"), ("dscale", "hermite"), ("cscale", "spline36"), ("deband", "no"),
            ("dither-depth", "auto"), ("correct-downscaling", "yes"), ("linear-downscaling", "no"), ("sigmoid-upscaling", "no"),
        },
    };

    private static IEnumerable<string> QualityArgs(QualityPreset q) =>
        QualityProps(q == QualityPreset.Auto ? QualityPreset.Balanced : q).Select(p => $"--{p.Prop}={p.Value}");

    private string BuildPlaylist()
    {
        var folder = Wallpaper.Source;
        var images = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(f => Media.FileValidator.IsImageExtension(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        var path = Path.Combine(AppPaths.Temp, $"slides-{Wallpaper.Id}.m3u");
        Directory.CreateDirectory(AppPaths.Temp);
        File.WriteAllLines(path, images);
        return path;
    }

    private void OnProperty(string name, JsonElement? data)
    {
        if (name == "estimated-vf-fps" && data is { ValueKind: JsonValueKind.Number } d) CurrentFps = d.GetDouble();
        else if (name == "pause" && data is { } p && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False))
        {
            if (State is PlaybackState.Playing or PlaybackState.Paused)
                State = p.GetBoolean() ? PlaybackState.Paused : PlaybackState.Playing;
            if (State == PlaybackState.Paused) CurrentFps = 0;
        }
    }

    private void OnEvent(string name, JsonElement e)
    {
        if (name == "end-file" && e.TryGetProperty("reason", out var r) && r.GetString() == "error")
        {
            var msg = e.TryGetProperty("file_error", out var fe) ? fe.GetString() : "unknown error";
            Log.Warn($"mpv could not play '{Wallpaper.Title}': {msg}");
            Fault("Playback error: " + msg);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (_disposed) return;
        int code = -1;
        try { code = _process?.ExitCode ?? -1; } catch { }
        Fault($"mpv exited unexpectedly (code {code})");
    }

    private void Fault(string reason)
    {
        if (State == PlaybackState.Faulted || _disposed) return;
        State = PlaybackState.Faulted;
        CurrentFps = 0;
        Faulted?.Invoke(this, reason);
    }

    public async Task PauseAsync(bool deep)
    {
        if (_ipc == null || State is PlaybackState.Faulted or PlaybackState.Stopped) return;
        await _ipc.SetAsync("pause", true).ConfigureAwait(false);
        State = PlaybackState.Paused;
        CurrentFps = 0;
        if (deep && _process is { HasExited: false } p)
        {
            try
            {
                // Drop the demuxer cache and trim the decoder's working set; resume costs a few ms of re-read.
                if (Wallpaper.FileSize > 48L * 1024 * 1024) await _ipc.SetAsync("demuxer-max-back-bytes", "1MiB").ConfigureAwait(false);
                Win32.TrimWorkingSet(p.Handle);
                Win32.SetEcoQos(p.Handle, true);
                p.PriorityClass = ProcessPriorityClass.Idle;
            }
            catch { }
        }
    }

    public async Task ResumeAsync()
    {
        if (_ipc == null || State is PlaybackState.Faulted or PlaybackState.Stopped) return;
        if (_process is { HasExited: false } p)
        {
            try { Win32.SetEcoQos(p.Handle, false); p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
        }
        await _ipc.SetAsync("pause", false).ConfigureAwait(false);
        State = PlaybackState.Playing;
    }

    public async Task ApplyOptionsAsync(RenderOptions o)
    {
        var old = Options;
        Options = o;
        if (_ipc == null) return;
        try
        {
            if (old.Scaling != o.Scaling)
            {
                var s = ScalingArgs(o.Scaling).Select(x => x[2..].Split('=', 2)).ToList();
                foreach (var kv in s) await _ipc.SetAsync(kv[0], kv[1]).ConfigureAwait(false);
            }
            if (old.Quality != o.Quality)
                foreach (var (prop, value) in QualityProps(o.Quality == QualityPreset.Auto ? QualityPreset.Balanced : o.Quality))
                    await _ipc.SetAsync(prop, value).ConfigureAwait(false);
            if (Math.Abs(old.Speed - o.Speed) > 0.001) await _ipc.SetAsync("speed", o.Speed).ConfigureAwait(false);

            var wantAudio = !o.Muted && o.Volume > 0;
            var hadAudio = !old.Muted && old.Volume > 0;
            if (wantAudio != hadAudio) await _ipc.SetAsync("aid", wantAudio ? "auto" : "no").ConfigureAwait(false);
            if (wantAudio) await _ipc.SetAsync("volume", o.Volume).ConfigureAwait(false);

            var newPlan = EffectsCompiler.ForMpv(o.Effects, o.HeavyEffectsAllowed);
            if (!SamePlan(newPlan, _effects))
            {
                var copyChanged = newPlan.NeedsCopyBack != _effects.NeedsCopyBack;
                _effects = newPlan;
                await ApplyEffectsAsync().ConfigureAwait(false);
                if (copyChanged) await _ipc.SetAsync("hwdec", HwDec(o.HardwareDecoding, _effects.NeedsCopyBack)).ConfigureAwait(false);
            }
            else if (old.HardwareDecoding != o.HardwareDecoding)
                await _ipc.SetAsync("hwdec", HwDec(o.HardwareDecoding, _effects.NeedsCopyBack)).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn("Applying mpv options failed: " + ex.Message); }
    }

    private static bool SamePlan(MpvEffectPlan a, MpvEffectPlan b) =>
        a.VideoFilter == b.VideoFilter && a.Shaders.SequenceEqual(b.Shaders) &&
        a.ShaderOpts.Count == b.ShaderOpts.Count && a.ShaderOpts.All(kv => b.ShaderOpts.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private async Task ApplyEffectsAsync()
    {
        if (_ipc == null) return;
        await _ipc.SetAsync("glsl-shader-opts", _effects.ShaderOpts).ConfigureAwait(false);
        await _ipc.SetAsync("glsl-shaders", _effects.Shaders.ToArray()).ConfigureAwait(false);
        await _ipc.CommandAsync("vf", "remove", "@wfmblur").ContinueWith(_ => { }).ConfigureAwait(false);
        if (_effects.VideoFilter != null) await _ipc.CommandAsync("vf", "add", _effects.VideoFilter).ConfigureAwait(false);
    }

    public void Resize() { /* mpv tracks the parent HWND size itself */ }

    /// <summary>
    /// Audio reaction for video wallpapers: cheap per-frame uniforms only — a subtle zoom pulse and a
    /// brightness/saturation glow. Particle bursts and waves need the web runtime.
    /// </summary>
    public void PostAudio(float[] left, float[] right, float level)
    {
        if (_ipc == null || State != PlaybackState.Playing) return;
        var style = Options.Effects.AudioStyle;
        if (!style.AnyEnabled && !Wallpaper.IsAudioReactive) return;
        float bass = 0;
        for (int i = 0; i < Math.Min(6, left.Length); i++) bass = Math.Max(bass, (left[i] + (i < right.Length ? right[i] : 0)) * 0.5f);
        var k = Math.Clamp(bass * style.Sensitivity, 0, 1);
        if (style.Pulse || Wallpaper.IsAudioReactive)
        {
            var zoom = 0.035 * k;
            if (Math.Abs(zoom - _zoomBase) > 0.002) { _zoomBase = zoom; _ipc.Send("set_property", "video-zoom", zoom); }
        }
        if (style.Glow)
        {
            _ipc.Send("set_property", "brightness", (int)(k * 12));
            _ipc.Send("set_property", "saturation", (int)(k * 15));
        }
    }

    public void PostPointer(PointerInput input) { }
    public void PostKey(KeyInput input) { }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        State = PlaybackState.Stopped;
        try { if (_ipc?.IsConnected == true) await _ipc.CommandAsync("quit").ConfigureAwait(false); } catch { }
        if (_ipc != null) await _ipc.DisposeAsync().ConfigureAwait(false);
        if (_process != null)
        {
            try
            {
                if (!_process.WaitForExit(800)) _process.Kill(entireProcessTree: true);
            }
            catch { }
            _process.Dispose();
        }
        if (_playlistFile != null) { try { File.Delete(_playlistFile); } catch { } }
    }
}
