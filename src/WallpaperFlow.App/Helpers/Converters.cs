using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallpaperFlow.App.Helpers;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool v = value switch { bool b => b, int i => i != 0, string s => s.Length > 0, null => false, _ => true };
        if (Invert ^ (p as string == "invert")) v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is Visibility.Visible;
}

/// <summary>Binds a RadioButton to an enum value: IsChecked="{Binding Mode, Converter={StaticResource EnumBool}, ConverterParameter=Dark}".</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && p is string s ? Enum.Parse(t.IsGenericType ? Nullable.GetUnderlyingType(t)! : t, s) : Binding.DoNothing;
}

/// <summary>Loads thumbnails without locking the file and decoded at display size.</summary>
public sealed class PathToImageConverter : IValueConverter
{
    public int DecodeWidth { get; set; } = 480;
    public object? Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not string path || path.Length == 0) return null;
        try
        {
            bool remote = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!remote && !File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = remote ? BitmapCacheOption.Default : BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.EndInit();
            if (!remote) bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var b = new SolidColorBrush(ThemeManager.Parse(value as string, Colors.Gray));
        b.Freeze();
        return b;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.Length == 2 && values[0] is double pct && values[1] is double width ? Math.Max(0, Math.Min(1, pct / 100.0)) * width : 0.0;
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
