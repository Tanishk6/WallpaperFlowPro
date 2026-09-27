using System.Runtime.InteropServices;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Desktop;

/// <summary>
/// Turns broadcast window messages on the engine window into typed events. Display changes are
/// debounced because docking/undocking produces a burst of WM_DISPLAYCHANGE / WM_SETTINGCHANGE.
/// </summary>
public sealed class SystemEventsHub : IMessageHook, IDisposable
{
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    public static readonly Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    public static readonly Guid GUID_BATTERY_PERCENTAGE_REMAINING = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
    public static readonly Guid GUID_ENERGY_SAVER_STATUS = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");

    private readonly EngineThread _engine;
    private readonly List<nint> _powerRegistrations = new();
    private readonly Timer _displayDebounce;

    public event Action? DisplaysChanged;
    public event Action? ExplorerRestarted;
    public event Action? Suspending;
    public event Action? Resumed;
    public event Action<Guid, uint>? PowerSettingChanged;
    public event Action? PowerStatusChanged;
    public event Action<bool>? SessionLockChanged;
    public event Action<bool>? RemoteSessionChanged;
    public event Action<string>? SettingChanged;

    public SystemEventsHub(EngineThread engine)
    {
        _engine = engine;
        _displayDebounce = new Timer(_ => _engine.Post(() => DisplaysChanged?.Invoke()), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Must run on the engine thread.</summary>
    public void Register()
    {
        _engine.AddHook(this);
        var hwnd = _engine.MessageWindow;
        foreach (var g in new[] { GUID_CONSOLE_DISPLAY_STATE, GUID_ACDC_POWER_SOURCE, GUID_BATTERY_PERCENTAGE_REMAINING, GUID_ENERGY_SAVER_STATUS })
        {
            var guid = g;
            var h = Win32.RegisterPowerSettingNotification(hwnd, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
            if (h != 0) _powerRegistrations.Add(h);
        }
        Win32.WTSRegisterSessionNotification(hwnd, 0 /* NOTIFY_FOR_THIS_SESSION */);
    }

    public bool HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (msg == _engine.TaskbarCreatedMessage && msg != 0)
        {
            Log.Info("Explorer restarted (TaskbarCreated)");
            ExplorerRestarted?.Invoke();
            return false; // let other hooks (tray icon) see it too
        }
        switch (msg)
        {
            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_DPICHANGED:
                _displayDebounce.Change(600, Timeout.Infinite);
                break;
            case Win32.WM_SETTINGCHANGE:
                var area = lParam != 0 ? Marshal.PtrToStringUni(lParam) ?? "" : "";
                if (wParam == 0x2F /* SPI_SETWORKAREA */) _displayDebounce.Change(600, Timeout.Infinite);
                SettingChanged?.Invoke(area);
                break;
            case Win32.WM_POWERBROADCAST:
                switch ((uint)wParam)
                {
                    case Win32.PBT_APMSUSPEND: Suspending?.Invoke(); break;
                    case Win32.PBT_APMRESUMEAUTOMATIC: Resumed?.Invoke(); break;
                    case Win32.PBT_APMPOWERSTATUSCHANGE: PowerStatusChanged?.Invoke(); break;
                    case Win32.PBT_POWERSETTINGCHANGE when lParam != 0:
                        var s = Marshal.PtrToStructure<Win32.POWERBROADCAST_SETTING>(lParam);
                        PowerSettingChanged?.Invoke(s.PowerSetting, s.Data);
                        break;
                }
                result = 1;
                return true;
            case Win32.WM_WTSSESSION_CHANGE:
                switch ((int)wParam)
                {
                    case Win32.WTS_SESSION_LOCK: SessionLockChanged?.Invoke(true); break;
                    case Win32.WTS_SESSION_UNLOCK: SessionLockChanged?.Invoke(false); break;
                    case Win32.WTS_REMOTE_CONNECT: RemoteSessionChanged?.Invoke(true); break;
                    case Win32.WTS_REMOTE_DISCONNECT: RemoteSessionChanged?.Invoke(false); break;
                }
                break;
        }
        return false;
    }

    public void Dispose()
    {
        _displayDebounce.Dispose();
        _engine.RemoveHook(this);
        foreach (var h in _powerRegistrations) Win32.UnregisterPowerSettingNotification(h);
        if (_engine.MessageWindow != 0) Win32.WTSUnRegisterSessionNotification(_engine.MessageWindow);
    }
}
