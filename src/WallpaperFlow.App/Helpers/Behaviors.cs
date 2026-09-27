using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WallpaperFlow.App.Helpers;

/// <summary>Plays a card's 4-second preview clip after hovering 350 ms. Only one clip plays at a time.</summary>
public static class HoverPreview
{
    public static bool Enabled { get; set; } = true;
    public static readonly DependencyProperty ClipProperty =
        DependencyProperty.RegisterAttached("Clip", typeof(string), typeof(HoverPreview), new PropertyMetadata(null, OnClipChanged));
    public static string? GetClip(DependencyObject d) => (string?)d.GetValue(ClipProperty);
    public static void SetClip(DependencyObject d, string? v) => d.SetValue(ClipProperty, v);

    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private static Panel? _pending, _host;
    private static MediaElement? _player;

    static HoverPreview() => Timer.Tick += (_, _) => { Timer.Stop(); if (_pending != null) Start(_pending); };

    private static void OnClipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel p) return;
        p.MouseEnter -= Enter; p.MouseLeave -= Leave;
        if (e.NewValue is string s && s.Length > 0) { p.MouseEnter += Enter; p.MouseLeave += Leave; }
    }

    private static void Enter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!Enabled) return;
        _pending = (Panel)sender;
        Timer.Stop(); Timer.Start();
    }

    private static void Leave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        Timer.Stop();
        _pending = null;
        Stop();
    }

    private static void Start(Panel host)
    {
        Stop();
        var clip = GetClip(host);
        if (string.IsNullOrEmpty(clip) || !System.IO.File.Exists(clip)) return;
        _player = new MediaElement
        {
            Source = new Uri(clip), IsMuted = true, LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Opacity = 0,
        };
        _player.MediaOpened += (s, _) => ((MediaElement)s!).Opacity = 1;
        _player.MediaEnded += (s, _) => { var m = (MediaElement)s!; m.Position = TimeSpan.Zero; m.Play(); };
        _host = host;
        host.Children.Add(_player);
        _player.Play();
    }

    private static void Stop()
    {
        if (_player == null) return;
        _player.Stop();
        _player.Close();
        _host?.Children.Remove(_player);
        _player = null;
        _host = null;
    }
}

/// <summary>Rounded clipping for any element (Border.CornerRadius doesn't clip children).</summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnChanged));
    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double v) => d.SetValue(RadiusProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= Update;
        fe.SizeChanged += Update;
        Apply(fe);
    }
    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);
    private static void Apply(FrameworkElement fe)
    {
        var r = GetRadius(fe);
        fe.Clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
    }
}

/// <summary>Lightweight line chart for the performance page (no charting library, draws in OnRender).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values;
        double w = ActualWidth, h = ActualHeight;
        if (v == null || v.Count < 2 || w <= 0 || h <= 0) return;
        double max = Math.Max(Maximum, v.Max()) * 1.05;
        int n = Math.Max(v.Count, 60);
        Point P(int i) => new(w - (v.Count - 1 - i) * (w / (n - 1)), h - Math.Clamp(v[i] / max, 0, 1) * (h - 2) - 1);
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (int i = 1; i < v.Count; i++) g.LineTo(P(i), true, true);
        }
        line.Freeze();
        if (Fill != null)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(P(0).X, h), true, true);
                for (int i = 0; i < v.Count; i++) g.LineTo(P(i), true, true);
                g.LineTo(new Point(P(v.Count - 1).X, h), true, true);
            }
            area.Freeze();
            dc.DrawGeometry(Fill, null, area);
        }
        var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
