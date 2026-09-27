using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WallpaperFlow.App.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void OnPropertiesChanged(params string[] names) { foreach (var n in names) OnPropertyChanged(n); }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null) : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Async command that disables itself while running and surfaces errors through a callback.</summary>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;
    public AsyncRelayCommand(Func<Task> execute, Action<Exception>? onError = null, Func<bool>? canExecute = null)
        : this(_ => execute(), onError, canExecute == null ? null : _ => canExecute()) { }
    public AsyncRelayCommand(Func<object?, Task> execute, Action<Exception>? onError = null, Func<object?, bool>? canExecute = null)
    { _execute = execute; _onError = onError; _canExecute = canExecute; }
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) Refresh(); } }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        IsRunning = true;
        try { await _execute(parameter); }
        catch (Exception ex) { if (_onError != null) _onError(ex); else throw; }
        finally { IsRunning = false; }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
