using System.Runtime.CompilerServices;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.ViewModels;

public abstract class PageViewModel : ObservableObject, IDisposable
{
    protected readonly AppServices S;
    protected readonly IUiDispatcher Ui;
    protected readonly IDialogService Dialogs;
    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    protected PageViewModel(AppServices services, IUiDispatcher ui, IDialogService dialogs)
    {
        S = services;
        Ui = ui;
        Dialogs = dialogs;
        DismissMessage = new RelayCommand(() => Message = null);
    }

    public abstract string Title { get; }
    public RelayCommand DismissMessage { get; }

    /// <summary>Inline banner text at the top of the page (errors and confirmations, never modal).</summary>
    public string? Message { get => _message; set => SetProperty(ref _message, value); }
    public bool MessageIsError { get => _messageIsError; set => SetProperty(ref _messageIsError, value); }
    public bool IsBusy { get => _busy; set => SetProperty(ref _busy, value); }

    protected void Say(string text) => Ui.Post(() => { MessageIsError = false; Message = text; });
    protected void Fail(Exception ex)
    {
        Log.Warn($"{Title}: {ex.Message}");
        Ui.Post(() => { MessageIsError = true; Message = ex.Message; });
    }

    protected AppSettings Cfg => S.Settings.Current;

    /// <summary>Two-way settings binding helper: mutate a section and notify the property.</summary>
    protected void Update(string section, Action<AppSettings> change, [CallerMemberName] string? property = null)
    {
        S.Settings.Update(section, change);
        OnPropertyChanged(property);
    }

    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }
    public virtual void Dispose() { }

    protected static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
