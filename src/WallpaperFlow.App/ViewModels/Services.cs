using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.ViewModels;

// UI abstractions so every view model stays free of WPF types (and is compile-checked/tested on its own).

public interface IUiDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Action action);
}

public interface IDialogService
{
    string[]? PickFiles(string title, string filter, bool multiple = true);
    string? PickFolder(string title);
    string? SaveFile(string title, string filter, string defaultName);
    bool Confirm(string title, string message, string confirmText = "OK");
    void Info(string title, string message);
    string? Prompt(string title, string label, string initial = "");
    void OpenFolder(string path);
    void OpenUrl(string url);
    void CopyText(string text);
}

public interface IThemeApplier
{
    void Apply(ThemeDefinition theme, ThemeMode mode, BackdropKind backdrop);
    bool IsDark { get; }
}
