using Microsoft.Win32;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Platform;

/// <summary>Per-user autostart via HKCU\...\Run (no admin rights, no scheduled task).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WallpaperFlowPro";

    public static string Command => $"\"{Environment.ProcessPath}\" --silent";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(ValueName, Command);
            else k.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Warn("Could not change autostart: " + ex.Message); }
    }
}
