using System.Runtime.InteropServices;
using WallpaperFlow.Core.Desktop;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Native;
using WallpaperFlow.Core.Rendering;

namespace WallpaperFlow.Core.Input;

/// <summary>
/// Forwards mouse (and optionally keyboard) input to interactive wallpapers. The wallpaper sits behind the
/// desktop icons so it never receives input itself; a low-level hook observes input over the desktop and
/// forwards it. Hooks are installed only while an interactive wallpaper is playing, never block or modify
/// input, and do the minimum inside the hook callback (the heavy work is posted to the engine queue).
/// </summary>
public sealed class InputRouter : IDisposable
{
    private readonly EngineThread _engine;
    private readonly Func<InteractionSettings> _settings;
    private readonly Action<string, int, int, int, int> _pointer;
    private readonly Action<KeyInput> _key;
    private nint _mouseHook, _keyHook;
    private Win32.HookProc? _mouseProc, _keyProc;
    private long _lastMoveTicks;
    private bool _overDesktop;

    public InputRouter(EngineThread engine, Func<InteractionSettings> settings, Action<string, int, int, int, int> pointer, Action<KeyInput> key)
    {
        _engine = engine;
        _settings = settings;
        _pointer = pointer;
        _key = key;
    }

    /// <summary>Install or remove hooks to match what the running wallpapers need.</summary>
    public void Update(bool interactiveWallpaperActive)
    {
        _engine.Post(() =>
        {
            var cfg = _settings();
            SetMouse(interactiveWallpaperActive && cfg.MouseEnabled);
            SetKeyboard(interactiveWallpaperActive && cfg.KeyboardEnabled);
        });
    }

    private void SetMouse(bool on)
    {
        if (on && _mouseHook == 0)
        {
            _mouseProc = MouseProc;
            _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, Win32.GetModuleHandle(null), 0);
            Log.Info(_mouseHook != 0 ? "Mouse forwarding on" : "Mouse hook failed: " + Marshal.GetLastWin32Error());
        }
        else if (!on && _mouseHook != 0)
        {
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
            Log.Info("Mouse forwarding off");
        }
    }

    private void SetKeyboard(bool on)
    {
        if (on && _keyHook == 0)
        {
            _keyProc = KeyProc;
            _keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyProc, Win32.GetModuleHandle(null), 0);
        }
        else if (!on && _keyHook != 0)
        {
            Win32.UnhookWindowsHookEx(_keyHook);
            _keyHook = 0;
        }
    }

    private nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                uint msg = (uint)wParam;
                string? kind = msg switch
                {
                    Win32.WM_MOUSEMOVE => "move",
                    Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN => "down",
                    Win32.WM_LBUTTONUP or Win32.WM_RBUTTONUP => "up",
                    Win32.WM_MOUSEWHEEL => "wheel",
                    _ => null,
                };
                if (kind != null)
                {
                    bool throttled = false;
                    if (kind == "move")
                    {
                        var now = Environment.TickCount64;
                        throttled = now - _lastMoveTicks < 8;
                        if (!throttled) _lastMoveTicks = now;
                    }
                    if (!throttled)
                    {
                        var over = DesktopHost.IsDesktopWindow(Win32.WindowFromPoint(info.pt));
                        if (over || _overDesktop)
                        {
                            // Tell the page the pointer left so hover effects end cleanly.
                            var k = over ? kind : "leave";
                            int button = msg is Win32.WM_RBUTTONDOWN or Win32.WM_RBUTTONUP ? 2 : 0;
                            int delta = msg == Win32.WM_MOUSEWHEEL ? (short)(info.mouseData >> 16) : 0;
                            _pointer(k, info.pt.X, info.pt.Y, button, delta);
                        }
                        _overDesktop = over;
                    }
                }
            }
            catch { /* never let an exception escape a hook */ }
        }
        return Win32.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private nint KeyProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                // Only while the desktop itself has focus — never observe typing into other apps.
                if (DesktopHost.IsDesktopWindow(Win32.GetForegroundWindow()))
                {
                    var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                    uint msg = (uint)wParam;
                    var kind = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN ? "down" : "up";
                    _key(new KeyInput(kind, (int)info.vkCode, KeyName(info.vkCode)));
                }
            }
            catch { }
        }
        return Win32.CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    internal static string KeyName(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)(vk + 32)).ToString(),
        0x20 => " ", 0x0D => "Enter", 0x1B => "Escape", 0x08 => "Backspace", 0x09 => "Tab",
        0x25 => "ArrowLeft", 0x26 => "ArrowUp", 0x27 => "ArrowRight", 0x28 => "ArrowDown",
        0x10 or 0xA0 or 0xA1 => "Shift", 0x11 or 0xA2 or 0xA3 => "Control",
        _ => "Unidentified",
    };

    public void Dispose()
    {
        _engine.InvokeAsync(() => { SetMouse(false); SetKeyboard(false); }).Wait(1000);
    }
}
