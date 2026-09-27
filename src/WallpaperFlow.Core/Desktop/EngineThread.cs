using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Desktop;

/// <summary>A component that wants to see messages sent to the engine's hidden top-level window.</summary>
public interface IMessageHook
{
    /// <returns>true if handled (then <paramref name="result"/> is returned to Windows).</returns>
    bool HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam, out nint result);
}

/// <summary>
/// Dedicated STA thread with a Win32 message loop. It owns every window we parent into the desktop
/// (WorkerW/Progman), WebView2 controllers, low-level hooks and WinEvent hooks. Keeping them off the WPF
/// UI thread means Explorer's input queue is never attached to the UI thread, and the UI can be torn
/// down completely (to save RAM) while wallpapers keep running.
/// </summary>
public sealed class EngineThread : IDisposable
{
    private const uint WM_INVOKE = Win32.WM_APP + 1;
    private const string ClassName = "WallpaperFlow.Engine";

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly List<IMessageHook> _hooks = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Win32.WndProc? _wndProc; // keep delegate alive
    private volatile bool _disposed;

    public nint MessageWindow { get; private set; }
    public uint TaskbarCreatedMessage { get; private set; }
    public int ManagedThreadId => _thread.ManagedThreadId;
    public SynchronizationContext? Context { get; private set; }

    public EngineThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "WallpaperFlow.Engine" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public Task StartAsync()
    {
        _thread.Start();
        return _ready.Task;
    }

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public void AddHook(IMessageHook hook) { lock (_hooks) _hooks.Add(hook); }
    public void RemoveHook(IMessageHook hook) { lock (_hooks) _hooks.Remove(hook); }

    public void Post(Action action)
    {
        if (_disposed) return;
        _queue.Enqueue(action);
        Win32.PostMessage(MessageWindow, WM_INVOKE, 0, 0);
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (CheckAccess())
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Runs an async function whose continuations stay on the engine thread.</summary>
    public Task InvokeAsync(Func<Task> func) => InvokeAsync<Task>(func).Unwrap();
    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => InvokeAsync<Task<T>>(func).Unwrap();

    private void Run()
    {
        try
        {
            Win32.SetThreadDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            _wndProc = WndProc;
            var hInst = Win32.GetModuleHandle(null);
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                lpszClassName = ClassName,
            };
            Win32.RegisterClassEx(ref wc);
            // A hidden *top-level* window (not HWND_MESSAGE): broadcasts such as TaskbarCreated,
            // WM_DISPLAYCHANGE and WM_SETTINGCHANGE are only delivered to top-level windows.
            MessageWindow = Win32.CreateWindowEx(Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE, ClassName, "WallpaperFlow Engine",
                Win32.WS_POPUP, 0, 0, 0, 0, 0, 0, hInst, 0);
            if (MessageWindow == 0) throw new InvalidOperationException("Could not create engine message window: " + Marshal.GetLastWin32Error());
            TaskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");

            Context = new EngineSynchronizationContext(this);
            SynchronizationContext.SetSynchronizationContext(Context);
            _ready.TrySetResult();

            while (Win32.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Engine thread crashed", ex);
            _ready.TrySetException(ex);
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_INVOKE)
        {
            Drain();
            return 0;
        }

        IMessageHook[] hooks;
        lock (_hooks) hooks = _hooks.ToArray();
        foreach (var h in hooks)
        {
            try
            {
                if (h.HandleMessage(hwnd, msg, wParam, lParam, out var result)) return result;
            }
            catch (Exception ex) { Log.Error($"Message hook {h.GetType().Name} failed", ex); }
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Drain()
    {
        // Bounded drain so a flood of posts can't starve input/paint messages.
        for (int i = 0; i < 256 && _queue.TryDequeue(out var a); i++)
        {
            try { a(); }
            catch (Exception ex) { Log.Error("Engine work item failed", ex); }
        }
        if (!_queue.IsEmpty) Win32.PostMessage(MessageWindow, WM_INVOKE, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        var hwnd = MessageWindow;
        if (hwnd != 0)
        {
            var done = InvokeAsync(() =>
            {
                Win32.DestroyWindow(hwnd);
                Win32.PostQuitMessage(0);
            });
            done.Wait(TimeSpan.FromSeconds(3));
        }
        _disposed = true;
        _thread.Join(TimeSpan.FromSeconds(3));
    }

    private sealed class EngineSynchronizationContext : SynchronizationContext
    {
        private readonly EngineThread _owner;
        public EngineSynchronizationContext(EngineThread owner) => _owner = owner;
        public override void Post(SendOrPostCallback d, object? state) => _owner.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_owner.CheckAccess()) d(state);
            else _owner.InvokeAsync(() => d(state)).GetAwaiter().GetResult();
        }
        public override SynchronizationContext CreateCopy() => this;
    }
}
