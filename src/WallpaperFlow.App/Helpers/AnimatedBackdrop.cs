using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.Helpers;

/// <summary>
/// The app window's animated background: soft aurora glow, a neon synthwave grid, drifting sakura petals under a night
/// sky, or a blurred copy of the wallpaper that's playing. Everything is vector shapes and gradients (no pixel-shader
/// blur), animated at 30 fps, follows the mouse slightly (parallax) and pauses while the window isn't active.
/// </summary>
public sealed class AnimatedBackdrop : Canvas
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(BackgroundScene), typeof(AnimatedBackdrop),
        new PropertyMetadata(BackgroundScene.Aurora, (d, _) => ((AnimatedBackdrop)d).ScheduleRebuild()));

    public static readonly DependencyProperty WallpaperPathProperty = DependencyProperty.Register(
        nameof(WallpaperPath), typeof(string), typeof(AnimatedBackdrop),
        new PropertyMetadata(null, (d, _) =>
        {
            var b = (AnimatedBackdrop)d;
            if (b.Scene == BackgroundScene.Wallpaper) b.ScheduleRebuild();
        }));

    public static readonly DependencyProperty IsDarkProperty = DependencyProperty.Register(
        nameof(IsDark), typeof(bool), typeof(AnimatedBackdrop),
        new PropertyMetadata(true, (d, _) => ((AnimatedBackdrop)d).ScheduleRebuild()));

    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(
        nameof(IsAnimated), typeof(bool), typeof(AnimatedBackdrop),
        new PropertyMetadata(true, (d, _) => ((AnimatedBackdrop)d).UpdateClocks()));

    public BackgroundScene Scene { get => (BackgroundScene)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public string? WallpaperPath { get => (string?)GetValue(WallpaperPathProperty); set => SetValue(WallpaperPathProperty, value); }
    public bool IsDark { get => (bool)GetValue(IsDarkProperty); set => SetValue(IsDarkProperty, value); }
    public bool IsAnimated { get => (bool)GetValue(IsAnimatedProperty); set => SetValue(IsAnimatedProperty, value); }

    private const int Fps = 30;
    private const double Bleed = 28;   // extra border so parallax never reveals an edge

    private readonly List<AnimationClock> _clocks = new();
    private readonly DispatcherTimer _rebuild;
    private TranslateTransform _parallax = new();
    private double _lastNx = 2, _lastNy = 2;
    private Random _rng = new(11);

    public AnimatedBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _rebuild = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _rebuild.Tick += (_, _) => { _rebuild.Stop(); Rebuild(); };
        SizeChanged += (_, _) => ScheduleRebuild();
        Loaded += (_, _) => ScheduleRebuild();
        Unloaded += (_, _) => { _rebuild.Stop(); Clear(); };
    }

    /// <summary>Shifts the scene slightly away from the pointer. nx, ny are -1..1 across the window.</summary>
    public void Parallax(double nx, double ny)
    {
        if (!IsAnimated || Scene == BackgroundScene.Glass) return;
        nx = Math.Clamp(nx, -1, 1);
        ny = Math.Clamp(ny, -1, 1);
        if (Math.Abs(nx - _lastNx) < 0.02 && Math.Abs(ny - _lastNy) < 0.02) return;
        _lastNx = nx;
        _lastNy = ny;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _parallax.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-nx * Bleed * 0.8, TimeSpan.FromMilliseconds(700)) { EasingFunction = ease });
        _parallax.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-ny * Bleed * 0.6, TimeSpan.FromMilliseconds(700)) { EasingFunction = ease });
    }

    private void ScheduleRebuild()
    {
        _rebuild.Stop();
        _rebuild.Start();
    }

    private void Clear()
    {
        foreach (var c in _clocks)
        {
            try { c.Controller?.Remove(); } catch { }
        }
        _clocks.Clear();
        Children.Clear();
    }

    private void UpdateClocks()
    {
        foreach (var c in _clocks)
        {
            var controller = c.Controller;
            if (controller == null) continue;
            if (IsAnimated) controller.Resume(); else controller.Pause();
        }
    }

    private void Rebuild()
    {
        Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10 || Scene == BackgroundScene.Glass) return;

        _rng = new Random(11);
        _parallax = new TranslateTransform();
        var layer = new Canvas { Width = w + 2 * Bleed, Height = h + 2 * Bleed, RenderTransform = _parallax };
        SetLeft(layer, -Bleed);
        SetTop(layer, -Bleed);
        Children.Add(layer);
        double W = layer.Width, H = layer.Height;

        var scene = Scene;
        if (scene == BackgroundScene.Wallpaper && string.IsNullOrEmpty(WallpaperPath)) scene = BackgroundScene.Aurora;
        switch (scene)
        {
            case BackgroundScene.Neon: BuildNeon(layer, W, H); break;
            case BackgroundScene.Sakura: BuildSakura(layer, W, H); break;
            case BackgroundScene.Wallpaper: BuildWallpaper(layer, W, H); break;
            default: BuildAurora(layer, W, H); break;
        }

        // Keeps text readable over any scene: a vignette in dark mode, a light wash in light mode.
        Children.Add(new Rectangle { Width = w, Height = h, Fill = Overlay() });
        UpdateClocks();
    }

    // ── Scenes ──────────────────────────────────────────────────────────────

    private void BuildAurora(Canvas l, double W, double H)
    {
        Base(l, W, H, IsDark ? new[] { "#070A18", "#0D1030", "#140A28" } : new[] { "#EEF1FB", "#E6E6FF", "#F6ECFA" }, vertical: false);
        double m = Math.Max(W, H);
        string[] palette = { "#6D5BFF", "#00C8FF", "#FF4FD8", "#3DFFB5", "#FF9E5E" };
        (double X, double Y, double S)[] spots = { (0.15, 0.2, 0.75), (0.85, 0.25, 0.7), (0.55, 0.85, 0.8), (0.2, 0.9, 0.55), (0.75, 0.6, 0.5) };
        for (int i = 0; i < spots.Length; i++)
        {
            var blob = Blob(l, spots[i].X * W, spots[i].Y * H, spots[i].S * m, Hex(palette[i]), IsDark ? (byte)0x6A : (byte)0x50);
            Drift(blob, W * 0.10, H * 0.10, 18 + i * 4.5, 0.12);
        }
        if (IsDark) Stars(l, W, H * 0.6, 28);
    }

    private void BuildNeon(Canvas l, double W, double H)
    {
        Base(l, W, H, new[] { "#06021A", "#150838", "#2A0B45" }, vertical: true);
        double horizon = H * 0.6;
        Stars(l, W, horizon * 0.85, 42);
        double m = Math.Max(W, H);
        Drift(Blob(l, W * 0.1, H * 0.18, m * 0.5, Hex("#00E5FF"), 0x3A), W * 0.05, H * 0.05, 22, 0.08);
        Drift(Blob(l, W * 0.9, H * 0.12, m * 0.5, Hex("#B04DFF"), 0x44), W * 0.05, H * 0.05, 26, 0.08);

        // Sun with the classic synthwave cut-outs, and its pulsing glow.
        double sun = Math.Min(W, H) * 0.36;
        var glow = Blob(l, W / 2, horizon - sun * 0.2, sun * 2.6, Hex("#FF3DA8"), 0x55);
        Animate(glow, OpacityProperty, 0.6, 1.0, 3.5);
        var sunBox = new Canvas { Width = sun, Height = sun, Clip = new EllipseGeometry(new Point(sun / 2, sun / 2), sun / 2, sun / 2) };
        sunBox.Children.Add(new Rectangle { Width = sun, Height = sun, Fill = Frozen(new LinearGradientBrush(Hex("#FFE27A"), Hex("#FF2F92"), 90)) });
        var cut = Frozen(new SolidColorBrush(Hex("#22093F")));
        for (int i = 0; i < 6; i++)
        {
            var bar = new Rectangle { Width = sun, Height = 2 + i * 1.8, Fill = cut };
            SetTop(bar, sun * 0.52 + i * sun * 0.08);
            sunBox.Children.Add(bar);
        }
        SetLeft(sunBox, W / 2 - sun / 2);
        SetTop(sunBox, horizon - sun * 0.74);
        l.Children.Add(sunBox);

        // Floor: perspective grid whose cross lines scroll toward the viewer.
        double fh = H - horizon;
        var floor = new Canvas { Width = W, Height = fh, ClipToBounds = true };
        SetTop(floor, horizon);
        floor.Children.Add(new Rectangle { Width = W, Height = fh, Fill = Frozen(new LinearGradientBrush(Hex("#1A0736"), Hex("#04020A"), 90)) });

        var grid = new Canvas { Width = W, Height = fh };
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.45));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 1));
        grid.OpacityMask = Frozen(fade);
        var lineBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0x3D, 0xD0)));

        var rays = new StreamGeometry();
        using (var g = rays.Open())
        {
            for (int i = -26; i <= 26; i++)
            {
                g.BeginFigure(new Point(W / 2 + i * 6, 0), false, false);
                g.LineTo(new Point(W / 2 + i * W * 0.085, fh), true, false);
            }
        }
        rays.Freeze();
        grid.Children.Add(new Path { Data = rays, Stroke = lineBrush, StrokeThickness = 1.2 });

        double gap = Math.Max(26, fh / 9);
        var crossGeo = new StreamGeometry();
        using (var g = crossGeo.Open())
        {
            for (double y = -gap; y <= fh + gap; y += gap)
            {
                g.BeginFigure(new Point(0, y), false, false);
                g.LineTo(new Point(W, y), true, false);
            }
        }
        crossGeo.Freeze();
        var cross = new Path { Data = crossGeo, Stroke = lineBrush, StrokeThickness = 1.2 };
        var scroll = new TranslateTransform();
        cross.RenderTransform = scroll;
        grid.Children.Add(cross);
        Animate(scroll, TranslateTransform.YProperty, 0, gap, 1.6, reverse: false, phase: 0);

        floor.Children.Add(grid);
        l.Children.Add(floor);

        var horizonLine = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        horizonLine.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x3D, 0xE8, 0xFF), 0));
        horizonLine.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x3D, 0xE8, 0xFF), 0.5));
        horizonLine.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x3D, 0xE8, 0xFF), 1));
        var hz = new Rectangle { Width = W, Height = 2, Fill = Frozen(horizonLine) };
        SetTop(hz, horizon - 1);
        l.Children.Add(hz);
    }

    private void BuildSakura(Canvas l, double W, double H)
    {
        Base(l, W, H, IsDark ? new[] { "#0D0A28", "#2A1446", "#5C2452" } : new[] { "#FFF1F5", "#FBE3EE", "#EFE6FF" }, vertical: true);
        double m = Math.Max(W, H);
        Drift(Blob(l, W * 0.8, H * 0.2, m * 0.6, Hex("#FF7EB6"), IsDark ? (byte)0x50 : (byte)0x48), W * 0.06, H * 0.05, 24, 0.1);
        Drift(Blob(l, W * 0.15, H * 0.8, m * 0.55, Hex("#8C6BFF"), IsDark ? (byte)0x4C : (byte)0x3C), W * 0.06, H * 0.05, 28, 0.1);

        if (IsDark)
        {
            Stars(l, W, H * 0.55, 26);
            double moon = Math.Min(W, H) * 0.11;
            var halo = Blob(l, W * 0.78, H * 0.2, moon * 4, Hex("#FFD9EA"), 0x45);
            Animate(halo, OpacityProperty, 0.7, 1.0, 5);
            var disc = new Ellipse { Width = moon, Height = moon, Fill = Frozen(new LinearGradientBrush(Hex("#FFF6FA"), Hex("#FFD3E6"), 60)) };
            SetLeft(disc, W * 0.78 - moon / 2);
            SetTop(disc, H * 0.2 - moon / 2);
            l.Children.Add(disc);
        }

        var petal = Geometry.Parse("M 0,5 C 3,0 9,0 12,3 C 11,4 11,6 12,7 C 9,10 3,10 0,5 Z");
        petal.Freeze();
        var fill = Frozen(new LinearGradientBrush(Hex("#FFE1EE"), Hex("#FF8FBF"), 45));
        int count = (int)Math.Clamp(W / 55, 14, 34);
        for (int i = 0; i < count; i++)
        {
            double size = 10 + _rng.NextDouble() * 14;
            var p = new Path
            {
                Data = petal, Fill = fill, Stretch = Stretch.Fill, Width = size, Height = size * 0.8,
                Opacity = 0.55 + _rng.NextDouble() * 0.4, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            SetLeft(p, _rng.NextDouble() * W - 80);
            SetTop(p, 0);
            var spin = new RotateTransform(_rng.NextDouble() * 360);
            var sway = new TranslateTransform();
            var fall = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(spin);
            group.Children.Add(sway);
            group.Children.Add(fall);
            p.RenderTransform = group;
            l.Children.Add(p);

            double seconds = 12 + _rng.NextDouble() * 12, phase = _rng.NextDouble();
            Animate(fall, TranslateTransform.YProperty, -40, H + 40, seconds, reverse: false, phase: phase);
            Animate(fall, TranslateTransform.XProperty, 0, 80 + _rng.NextDouble() * 160, seconds, reverse: false, phase: phase);
            Animate(sway, TranslateTransform.XProperty, -18, 18, 2.5 + _rng.NextDouble() * 2.5);
            Animate(spin, RotateTransform.AngleProperty, spin.Angle, spin.Angle + 360, 6 + _rng.NextDouble() * 8, reverse: false);
        }
    }

    private void BuildWallpaper(Canvas l, double W, double H)
    {
        Base(l, W, H, new[] { "#06080F", "#06080F" }, vertical: true);
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(WallpaperPath!, UriKind.Absolute);
            bmp.DecodePixelWidth = 48;   // decoding tiny and scaling up smoothly is a free, strong blur
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();
            var img = new Image
            {
                Source = bmp, Stretch = Stretch.UniformToFill, Width = W, Height = H,
                RenderTransformOrigin = new Point(0.5, 0.5), Opacity = IsDark ? 0.9 : 0.75,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var zoom = new ScaleTransform(1.1, 1.1);
            img.RenderTransform = zoom;
            l.Children.Add(img);
            Animate(zoom, ScaleTransform.ScaleXProperty, 1.1, 1.22, 40, phase: 0);
            Animate(zoom, ScaleTransform.ScaleYProperty, 1.1, 1.22, 40, phase: 0);
        }
        catch
        {
            BuildAurora(l, W, H);
            return;
        }
        var tint = IsDark
            ? new LinearGradientBrush(Color.FromArgb(0x8C, 6, 8, 16), Color.FromArgb(0xCC, 6, 8, 16), 90)
            : new LinearGradientBrush(Color.FromArgb(0x66, 0xF4, 0xF6, 0xFB), Color.FromArgb(0xA6, 0xF4, 0xF6, 0xFB), 90);
        l.Children.Add(new Rectangle { Width = W, Height = H, Fill = Frozen(tint) });
    }

    // ── Building blocks ─────────────────────────────────────────────────────

    private static void Base(Canvas l, double W, double H, string[] stops, bool vertical)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 1) };
        for (int i = 0; i < stops.Length; i++)
            g.GradientStops.Add(new GradientStop(Hex(stops[i]), stops.Length == 1 ? 0 : (double)i / (stops.Length - 1)));
        l.Children.Add(new Rectangle { Width = W, Height = H, Fill = Frozen(g) });
    }

    /// <summary>A soft glow: a radial gradient fading to transparent, which reads as a heavily blurred light.</summary>
    private static Ellipse Blob(Canvas l, double cx, double cy, double size, Color c, byte alpha)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.45), c.R, c.G, c.B), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        var e = new Ellipse { Width = size, Height = size, Fill = Frozen(brush), RenderTransformOrigin = new Point(0.5, 0.5) };
        SetLeft(e, cx - size / 2);
        SetTop(e, cy - size / 2);
        l.Children.Add(e);
        return e;
    }

    private void Drift(UIElement e, double dx, double dy, double seconds, double breathe)
    {
        var move = new TranslateTransform();
        var scale = new ScaleTransform(1, 1);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(move);
        e.RenderTransform = group;
        Animate(move, TranslateTransform.XProperty, -dx, dx, seconds);
        Animate(move, TranslateTransform.YProperty, -dy, dy, seconds * 1.37);
        if (breathe > 0)
        {
            double phase = _rng.NextDouble();
            Animate(scale, ScaleTransform.ScaleXProperty, 1 - breathe, 1 + breathe, seconds * 0.83, phase: phase);
            Animate(scale, ScaleTransform.ScaleYProperty, 1 - breathe, 1 + breathe, seconds * 0.83, phase: phase);
        }
    }

    private void Stars(Canvas l, double W, double H, int count)
    {
        var brush = Frozen(new SolidColorBrush(Colors.White));
        for (int i = 0; i < count; i++)
        {
            double s = 1 + _rng.NextDouble() * 2;
            var e = new Ellipse { Width = s, Height = s, Fill = brush, Opacity = 0.3 };
            SetLeft(e, _rng.NextDouble() * W);
            SetTop(e, _rng.NextDouble() * H);
            l.Children.Add(e);
            Animate(e, OpacityProperty, 0.1, 0.9, 2 + _rng.NextDouble() * 4);
        }
    }

    private Brush Overlay()
    {
        if (!IsDark) return Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xF4, 0xF6, 0xFB)));
        var v = new RadialGradientBrush { RadiusX = 0.75, RadiusY = 0.75 };
        v.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.55));
        v.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 0, 0, 0), 1));
        return Frozen(v);
    }

    /// <param name="phase">0..1 start position within the cycle; negative picks a random one so shapes don't move in lockstep.</param>
    private void Animate(IAnimatable target, DependencyProperty property, double from, double to, double seconds,
        bool reverse = true, double phase = -1)
    {
        var animation = new DoubleAnimation(from, to, new Duration(TimeSpan.FromSeconds(seconds)))
        {
            AutoReverse = reverse,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        if (reverse) animation.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        Timeline.SetDesiredFrameRate(animation, Fps);
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock);
        _clocks.Add(clock);
        double start = phase >= 0 ? phase : _rng.NextDouble();
        clock.Controller?.Seek(TimeSpan.FromSeconds(seconds * start * (reverse ? 2 : 1)), TimeSeekOrigin.BeginTime);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
