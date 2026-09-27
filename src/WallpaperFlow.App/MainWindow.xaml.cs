using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WallpaperFlow.App.Helpers;
using WallpaperFlow.App.ViewModels;

namespace WallpaperFlow.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ThemeManager _theme;

    public MainWindow(MainViewModel vm, ThemeManager theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        DataContext = vm;
        SourceInitialized += (_, _) => WindowEffects.Apply(this, _theme.IsDark, _theme.Backdrop);
        StateChanged += (_, _) => UpdateMaximized();
        _vm.PropertyChanged += OnVmChanged;
        _theme.Applied += OnThemeApplied;
        Closed += (_, _) => { _vm.PropertyChanged -= OnVmChanged; _theme.Applied -= OnThemeApplied; };
        // The background only animates while you're using the window, so it costs nothing in the background.
        Activated += (_, _) => Backdrop.IsAnimated = !_theme.ReduceMotion;
        Deactivated += (_, _) => Backdrop.IsAnimated = false;
        MouseMove += OnMouseMoveParallax;
        Backdrop.WallpaperPath = _vm.NowPlayingThumbnail;
        OnThemeApplied();
        InputBindings.Add(new KeyBinding(new Helpers.DelegateCommand(() => { Search.Focus(); Search.SelectAll(); }), Key.F, ModifierKeys.Control));
        UpdateIndicators();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsPaused) or nameof(MainViewModel.IsMuted) or nameof(MainViewModel.IsLive)) UpdateIndicators();
        else if (e.PropertyName == nameof(MainViewModel.NowPlayingThumbnail)) Backdrop.WallpaperPath = _vm.NowPlayingThumbnail;
        else if (e.PropertyName == nameof(MainViewModel.CurrentPage)) AnimatePageIn();
    }

    private void OnThemeApplied()
    {
        Backdrop.IsDark = _theme.IsDark;
        Backdrop.Scene = _theme.Scene;
        Backdrop.IsAnimated = IsActive && !_theme.ReduceMotion;
    }

    private void OnMouseMoveParallax(object sender, MouseEventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var p = e.GetPosition(this);
        Backdrop.Parallax(p.X / ActualWidth * 2 - 1, p.Y / ActualHeight * 2 - 1);
    }

    private void AnimatePageIn()
    {
        if (_theme.ReduceMotion) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        PageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    private void UpdateIndicators()
    {
        PauseBtn.Content = _vm.IsPaused ? "\uE768" : "\uE769";
        PauseBtn.ToolTip = _vm.IsPaused ? "Resume (Ctrl+Space)" : "Pause (Ctrl+Space)";
        MuteBtn.Content = _vm.IsMuted ? "\uE74F" : "\uE767";
        LiveDot.SetResourceReference(Shape.FillProperty, _vm.IsLive ? "HighlightBrush" : "TextFaintBrush");
    }

    private void UpdateMaximized()
    {
        // A WindowChrome window overhangs the screen by the resize border when maximised.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Creator_Click(object sender, RoutedEventArgs e) => new DialogService().OpenUrl(Core.AppInfo.CreatorUrl);
}
