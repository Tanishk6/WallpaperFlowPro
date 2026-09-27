using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WallpaperFlow.Core.Desktop;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Rendering;

/// <summary>
/// HTML / web URL / shader / clock renderer: a WebView2 controller hosted directly in our desktop host
/// window. All calls happen on the engine thread.
///
/// Sandbox: each local wallpaper gets its own origin (w&lt;id&gt;.wallpaperflow.example) mapped to its folder
/// with DenyCors; navigation away, popups, downloads, external protocols and permission prompts are
/// blocked; requests to other hosts get a 403 unless the user granted network access; devtools, context
/// menus, accelerator keys and host objects are disabled; web messages are size- and type-checked.
/// </summary>
public sealed class WebRenderer : IWallpaperRenderer
{
    private CoreWebView2Controller? _controller;
    private CoreWebView2Environment? _env;
    private string _virtualHost = "";
    private Uri? _startUri;
    private bool _disposed;
    private readonly bool _isRemote;

    public WallpaperItem Wallpaper { get; }
    public HostWindow Host { get; }
    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public RenderOptions Options { get; private set; }
    public double CurrentFps { get; private set; }
    public bool WantsAudio => Wallpaper.IsAudioReactive || Options.Effects.AudioStyle.AnyEnabled;
    public bool WantsInput => Wallpaper.IsInteractive && Options.InputEnabled;
    public IReadOnlyList<int> ProcessIds => WebViewEnvironment.ProcessIds;
    public event Action<IWallpaperRenderer, string>? Faulted;

    public WebRenderer(WallpaperItem wallpaper, HostWindow host, RenderOptions options)
    {
        Wallpaper = wallpaper;
        Host = host;
        Options = options;
        _isRemote = wallpaper.Type == WallpaperType.Url;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        State = PlaybackState.Loading;
        _env = await WebViewEnvironment.GetAsync();
        ct.ThrowIfCancellationRequested();
        _controller = await _env.CreateCoreWebView2ControllerAsync(Host.Handle);
        _controller.DefaultBackgroundColor = Color.Black;
        _controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
        _controller.ShouldDetectMonitorScaleChanges = false;
        _controller.RasterizationScale = Math.Max(0.5, Options.MonitorScale);
        Resize();

        var web = _controller.CoreWebView2;
        var s = web.Settings;
        s.IsScriptEnabled = true;
        s.IsWebMessageEnabled = true;
        s.AreDefaultScriptDialogsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsZoomControlEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        web.IsMuted = Options.Muted || Options.Volume <= 0;

        web.NavigationStarting += OnNavigationStarting;
        web.NewWindowRequested += (_, e) => e.Handled = true;
        web.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        web.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        web.WebMessageReceived += OnWebMessage;
        web.ProcessFailed += OnProcessFailed;
        web.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) Log.Warn($"Web wallpaper '{Wallpaper.Title}' failed to load: {e.WebErrorStatus}");
            PostConfig();
        };

        if (!_isRemote)
        {
            _virtualHost = WebViewEnvironment.VirtualHostFor(Wallpaper.Id);
            web.SetVirtualHostNameToFolderMapping(_virtualHost, Wallpaper.Source, CoreWebView2HostResourceAccessKind.DenyCors);
            if (!Options.AllowNetwork)
            {
                web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += OnResourceRequested;
            }
        }

        await web.AddScriptToExecuteOnDocumentCreatedAsync(
            $"window.__wallpaperflowBoot={BootJson()};\n" + WebViewEnvironment.RuntimeScript);

        _startUri = _isRemote
            ? new Uri(Wallpaper.Source)
            : new Uri($"https://{_virtualHost}/{Uri.EscapeDataString(Wallpaper.Entry ?? "index.html").Replace("%2F", "/")}");
        web.Navigate(_startUri.ToString());
        _controller.IsVisible = true;
        State = PlaybackState.Playing;
        Log.Info($"WebView2 wallpaper '{Wallpaper.Title}' → {_startUri}");
    }

    private string BootJson() => JsonSerializer.Serialize(ConfigPayload(), JsonDefaults.Compact);

    private Dictionary<string, object?> ConfigPayload() => new()
    {
        ["type"] = "config",
        ["fps"] = Options.FpsCap,
        ["renderScale"] = Options.RenderScale,
        ["effects"] = EffectsCompiler.ForWeb(Options.Effects, Options.HeavyEffectsAllowed),
        ["audioStyle"] = Options.Effects.AudioStyle,
        ["audioReactive"] = WantsAudio,
        ["interactive"] = WantsInput,
        ["network"] = Options.AllowNetwork || _isRemote,
        ["properties"] = string.IsNullOrWhiteSpace(Options.PropertiesJson) ? null : JsonDocument.Parse(Options.PropertiesJson).RootElement,
        ["wallpaper"] = new { id = Wallpaper.Id, title = Wallpaper.Title, type = Wallpaper.Type.ToString().ToLowerInvariant() },
    };

    private void PostConfig()
    {
        try { _controller?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(ConfigPayload(), JsonDefaults.Compact)); }
        catch (Exception ex) { Log.Debug("PostConfig failed: " + ex.Message); }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Cancel = true; return; }
        bool allowed = _isRemote
            ? _startUri != null && (uri.Scheme is "https" or "http") && string.Equals(uri.Host, _startUri.Host, StringComparison.OrdinalIgnoreCase)
            : uri.Scheme == "https" && string.Equals(uri.Host, _virtualHost, StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme is "about" or "data") allowed = e.IsUserInitiated == false;
        if (!allowed)
        {
            Log.Warn($"Blocked navigation from wallpaper '{Wallpaper.Title}' to {uri.Scheme}://{uri.Host}");
            e.Cancel = true;
        }
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is "data" or "blob") return;
        if (string.Equals(uri.Host, _virtualHost, StringComparison.OrdinalIgnoreCase)) return;
        e.Response = _env!.CreateWebResourceResponse(null, 403, "Blocked by WallpaperFlow sandbox", "Content-Type: text/plain");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; } catch { return; }
        if (json.Length > 4096) return; // wallpapers only send tiny status messages
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t)) return;
            switch (t.GetString())
            {
                case "fps" when root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number:
                    CurrentFps = Math.Clamp(v.GetDouble(), 0, 1000);
                    break;
                case "ready":
                    PostConfig();
                    break;
                case "error" when root.TryGetProperty("message", out var m):
                    var text = m.GetString() ?? "";
                    Log.Warn($"Wallpaper '{Wallpaper.Title}' script error: {(text.Length > 300 ? text[..300] : text)}");
                    break;
            }
        }
        catch { /* malformed message: ignore */ }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Warn($"WebView2 process failure ({e.ProcessFailedKind}) for '{Wallpaper.Title}'");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or CoreWebView2ProcessFailedKind.GpuProcessExited)
        {
            if (State == PlaybackState.Faulted || _disposed) return;
            State = PlaybackState.Faulted;
            Faulted?.Invoke(this, "Web content process failed: " + e.ProcessFailedKind);
        }
    }

    public async Task PauseAsync(bool deep)
    {
        if (_controller == null || State is PlaybackState.Faulted) return;
        Post("{\"type\":\"pause\"}");
        State = PlaybackState.Paused;
        CurrentFps = 0;
        if (deep)
        {
            try
            {
                _controller.IsVisible = false;
                var web = _controller.CoreWebView2;
                web.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                await web.TrySuspendAsync();
            }
            catch (Exception ex) { Log.Debug("WebView2 suspend failed: " + ex.Message); }
        }
    }

    public Task ResumeAsync()
    {
        if (_controller == null || State is PlaybackState.Faulted) return Task.CompletedTask;
        try
        {
            var web = _controller.CoreWebView2;
            if (web.IsSuspended) web.Resume();
            web.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            _controller.IsVisible = true;
        }
        catch (Exception ex) { Log.Debug("WebView2 resume failed: " + ex.Message); }
        Post("{\"type\":\"resume\"}");
        State = PlaybackState.Playing;
        return Task.CompletedTask;
    }

    public Task ApplyOptionsAsync(RenderOptions options)
    {
        var scaleChanged = Math.Abs(options.MonitorScale - Options.MonitorScale) > 0.01;
        Options = options;
        if (_controller != null)
        {
            try
            {
                _controller.CoreWebView2.IsMuted = options.Muted || options.Volume <= 0;
                if (scaleChanged) _controller.RasterizationScale = Math.Max(0.5, options.MonitorScale);
            }
            catch { }
        }
        PostConfig();
        return Task.CompletedTask;
    }

    public void Resize()
    {
        if (_controller == null) return;
        _controller.Bounds = new Rectangle(0, 0, Host.Width, Host.Height);
        _controller.NotifyParentWindowPositionChanged();
    }

    private void Post(string json)
    {
        if (_controller == null || State == PlaybackState.Faulted) return;
        try { _controller.CoreWebView2.PostWebMessageAsJson(json); } catch { }
    }

    public void PostAudio(float[] left, float[] right, float level)
    {
        if (State != PlaybackState.Playing) return;
        var sb = new System.Text.StringBuilder(1400);
        sb.Append("{\"type\":\"audio\",\"level\":").Append(level.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"l\":[");
        AppendArray(sb, left); sb.Append("],\"r\":["); AppendArray(sb, right); sb.Append("]}");
        Post(sb.ToString());
    }

    private static void AppendArray(System.Text.StringBuilder sb, float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(values[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public void PostPointer(PointerInput p)
    {
        if (!WantsInput || State != PlaybackState.Playing) return;
        Post($"{{\"type\":\"pointer\",\"kind\":\"{p.Kind}\",\"x\":{p.X},\"y\":{p.Y},\"button\":{p.Button},\"delta\":{p.Delta}}}");
    }

    public void PostKey(KeyInput k)
    {
        if (!WantsInput || State != PlaybackState.Playing) return;
        Post(JsonSerializer.Serialize(new { type = "key", kind = k.Kind, code = k.VirtualKey, key = k.Key }));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        State = PlaybackState.Stopped;
        try { _controller?.Close(); } catch { }
        _controller = null;
        return ValueTask.CompletedTask;
    }
}
