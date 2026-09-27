using System.Windows;
using System.Windows.Threading;
using WallpaperFlow.App.Helpers;
using WallpaperFlow.App.ViewModels;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Performance;
using WallpaperFlow.Core.Platform;

namespace WallpaperFlow.App;

/// <summary>
/// Tray-first lifetime: the process lives in the tray; the main window is created on demand and fully
/// destroyed on close (then the working set is trimmed), so an idle app costs a few tens of MB.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _single;
    private AppServices? _services;
    private MainWindow? _window;
    private ThemeManager? _theme;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new SingleInstance("WallpaperFlowPro");
        if (!_single.IsFirst)
        {
            _single.SignalFirstInstance(e.Args.Length > 0 ? string.Join(' ', e.Args) : "open");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) CrashGuard.WriteReport(ex, "AppDomain"); };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

        bool silent = e.Args.Contains("--silent");
        try
        {
            _services = new AppServices();
            if (e.Args.Contains("--safe-mode")) _services.Settings.Update("security", s => s.Security.SafeMode = true);
            _theme = new ThemeManager(_services);
            _theme.ApplyCurrent();
            _services.OpenRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
            _services.ExitRequested += () => Dispatcher.BeginInvoke(() => _ = ExitAsync());
            _services.Notify += (title, msg) => _services.Tray.ShowBalloon(title, msg);
            _single.Activated += arg => Dispatcher.BeginInvoke(() => { if (arg.Contains("exit")) _ = ExitAsync(); else ShowMainWindow(); });

            await _services.StartAsync();
            var s = _services.Settings.Current;
            if (!silent || !s.General.FirstRunCompleted) ShowMainWindow();
            if (!s.General.FirstRunCompleted) _services.Settings.Update("general", x => x.General.FirstRunCompleted = true);
        }
        catch (Exception ex)
        {
            var report = CrashGuard.WriteReport(ex, "Startup");
            MessageBox.Show($"WallpaperFlow Pro could not start:\n\n{ex.Message}\n\nDetails were saved to:\n{report}", "WallpaperFlow Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ShowMainWindow()
    {
        if (_services == null || _exiting) return;
        if (_window == null)
        {
            var vm = new MainViewModel(_services, new UiDispatcher(Dispatcher), new DialogService(), _theme!);
            _window = new MainWindow(vm, _theme!);
            _window.Closed += (_, _) =>
            {
                vm.Dispose();
                _window = null;
                if (_exiting) return;
                if (!_services.Settings.Current.General.MinimizeToTray) { _ = ExitAsync(); return; }
                // Give WPF a moment to release visuals, then hand memory back to Windows.
                Dispatcher.BeginInvoke(MemoryTrimmer.Trim, DispatcherPriority.ApplicationIdle);
            };
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.Close();
        if (_services != null) await _services.DisposeAsync();
        _single?.Dispose();
        Shutdown();
    }

    private bool _recovering;

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // UI errors must not take the wallpapers down: close the window, keep the engine running.
        e.Handled = true;
        Log.Error("UI exception", e.Exception);
        if (_recovering) return;              // one report and one close per burst of errors
        _recovering = true;
        var report = CrashGuard.WriteReport(e.Exception, "UI");
        // Close after the current layout pass; closing from inside it can fail and leave a half-built window.
        Dispatcher.BeginInvoke(() =>
        {
            try { _window?.Close(); } catch (Exception ex) { Log.Error("Closing window after UI error failed", ex); }
            _recovering = false;
            _services?.Tray.ShowBalloon("WallpaperFlow hit a problem",
                "The window was closed, wallpapers keep running. Open it again from the tray. Details: " + System.IO.Path.GetFileName(report));
        }, DispatcherPriority.Background);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && _services != null)
        {
            _exiting = true;
            _services.DisposeAsync().AsTask().Wait(5000);
        }
        base.OnExit(e);
    }
}
