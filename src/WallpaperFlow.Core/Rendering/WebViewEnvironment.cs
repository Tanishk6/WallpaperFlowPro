using Microsoft.Web.WebView2.Core;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Rendering;

/// <summary>
/// One shared WebView2 environment (one browser process, GPU process and at most two renderer
/// processes) for every web wallpaper. Created lazily on the engine thread.
/// </summary>
public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _env;
    private static int[] _processIds = Array.Empty<int>();
    private static string? _runtimeScript;

    /// <summary>
    /// Chromium flags tuned for wallpapers: occlusion tracking would otherwise throttle a window that sits
    /// behind the desktop icons to 0 fps; background networking/sync/components are useless here.
    /// </summary>
    public const string BrowserArguments =
        "--disable-features=CalculateNativeWinOcclusion,msSmartScreenProtection,msWebOOUI,msPdfOOUI,Translate,AutofillServerCommunication " +
        "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
        "--autoplay-policy=no-user-gesture-required --renderer-process-limit=2 " +
        "--disable-background-networking --disable-component-update --disable-sync --disable-default-apps --no-first-run";

    public static IReadOnlyList<int> ProcessIds => _processIds;

    public static string? RuntimeVersion
    {
        get { try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { return null; } }
    }

    public static bool IsRuntimeInstalled()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    public static Task<CoreWebView2Environment> GetAsync() => _env ??= CreateAsync();

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var options = new CoreWebView2EnvironmentOptions(BrowserArguments);
        var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2Data, options);
        env.ProcessInfosChanged += (_, _) => RefreshProcesses(env);
        RefreshProcesses(env);
        Log.Info("WebView2 environment ready, runtime " + env.BrowserVersionString);
        return env;
    }

    private static void RefreshProcesses(CoreWebView2Environment env)
    {
        try { _processIds = env.GetProcessInfos().Select(p => p.ProcessId).ToArray(); }
        catch { }
    }

    public static string RuntimeScript
    {
        get
        {
            if (_runtimeScript != null) return _runtimeScript;
            var path = Path.Combine(AppPaths.Runtime, "web", "wallpaperflow-runtime.js");
            _runtimeScript = File.Exists(path) ? File.ReadAllText(path) : "window.wallpaperflow={};";
            return _runtimeScript;
        }
    }

    /// <summary>Host name for a wallpaper's sandboxed origin. Uses the reserved .example TLD so it can never collide with a real site.</summary>
    public static string VirtualHostFor(string wallpaperId) =>
        "w" + new string(wallpaperId.Where(char.IsLetterOrDigit).Take(12).ToArray()).ToLowerInvariant() + ".wallpaperflow.example";
}
