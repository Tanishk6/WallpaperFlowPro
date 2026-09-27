using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using WallpaperFlow.App.ViewModels;

namespace WallpaperFlow.App.Helpers;

public sealed class UiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _d;
    public UiDispatcher(Dispatcher d) => _d = d;
    public void Post(Action action) => _d.BeginInvoke(action);
    public Task InvokeAsync(Action action) => _d.InvokeAsync(action).Task;
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public string[]? PickFiles(string title, string filter, bool multiple = true)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, Multiselect = multiple };
        return Show(d) ? d.FileNames : null;
    }

    public string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return Show(d) ? d.FolderName : null;
    }

    public string? SaveFile(string title, string filter, string defaultName)
    {
        var d = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        return Show(d) ? d.FileName : null;
    }

    private static bool Show(CommonDialog d) => (Owner is { } o ? d.ShowDialog(o) : d.ShowDialog()) == true;

    private static MessageBoxResult Box(string message, string title, MessageBoxButton buttons, MessageBoxImage icon) =>
        Owner is { } o ? MessageBox.Show(o, message, title, buttons, icon) : MessageBox.Show(message, title, buttons, icon);

    public bool Confirm(string title, string message, string confirmText = "OK") =>
        Box(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void Info(string title, string message) => Box(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? Prompt(string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 16), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(24), Children = { new TextBlock { Text = label }, box, buttons } };
        var win = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Owner, ShowInTaskbar = false,
        };
        win.SetResourceReference(Control.BackgroundProperty, "WindowSolidBrush");
        win.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }

    public void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
    public void OpenUrl(string url) { if (url.StartsWith("https://")) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    public void CopyText(string text) { try { Clipboard.SetText(text); } catch { } }
}

/// <summary>One instance per user session. A second launch forwards its arguments over a named pipe and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _cts = new();
    public bool IsFirst { get; }
    public event Action<string>? Activated;

    public SingleInstance(string name)
    {
        _mutex = new Mutex(true, $@"Local\{name}.SingleInstance", out var created);
        IsFirst = created;
        _pipe = $"{name}.{Environment.UserName}.activate";
        if (IsFirst) _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server);
                var msg = await reader.ReadLineAsync(_cts.Token);
                Activated?.Invoke(msg ?? "open");
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(250); }
        }
    }

    public void SignalFirstInstance(string message)
    {
        try
        {
            AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(message);
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}

public sealed class DelegateCommand : System.Windows.Input.ICommand
{
    private readonly Action _run;
    public DelegateCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}
