using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.ViewModels;

public sealed class WallpaperCardViewModel : ObservableObject
{
    private readonly LibraryRepository _library;
    private bool _active, _selected;
    public WallpaperItem Item { get; private set; }

    public WallpaperCardViewModel(WallpaperItem item, LibraryRepository library, bool active = false)
    {
        Item = item;
        _library = library;
        _active = active;
    }

    public string Id => Item.Id;
    public string Title => Item.Title;
    public string? Thumbnail => Item.Thumbnail;
    public string? PreviewClip => Item.PreviewClip;
    public bool IsAudioReactive => Item.IsAudioReactive;
    public bool IsInteractive => Item.IsInteractive;
    public bool HasQualityWarning => Item.QualityScore is < 50;
    public string? QualityFlags => Item.QualityFlags;
    public bool IsActive { get => _active; set => SetProperty(ref _active, value); }
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }

    public string Glyph => Item.Type switch
    {
        WallpaperType.Video => "\uE714",
        WallpaperType.Gif or WallpaperType.Apng => "\uE7F8",
        WallpaperType.Html or WallpaperType.Url => "\uE774",
        WallpaperType.Shader => "\uE790",
        WallpaperType.Clock => "\uE823",
        _ => "\uE8B9",
    };

    public string TypeLabel => Item.Type switch
    {
        WallpaperType.Gif => "GIF",
        WallpaperType.Apng => "APNG",
        WallpaperType.Html => "Web",
        WallpaperType.Url => "Website",
        _ => Item.Type.ToString(),
    };

    public string Meta
    {
        get
        {
            var parts = new List<string> { TypeLabel };
            if (Item.Width > 0) parts.Add(ResolutionLabel(Item.Width, Item.Height));
            if (Item.Duration > 0) parts.Add(TimeSpan.FromSeconds(Item.Duration).ToString(Item.Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
            if (Item.Fps >= 50) parts.Add($"{Math.Round(Item.Fps)} fps");
            return string.Join(" · ", parts);
        }
    }

    public static string ResolutionLabel(int w, int h) => w switch
    {
        >= 7680 => "8K",
        >= 5120 => "5K",
        >= 3840 => w >= 5000 ? "5K" : (double)w / h > 2.2 ? $"{w}×{h}" : "4K",
        >= 2560 => (double)w / h > 2.2 ? $"{w}×{h}" : "1440p",
        >= 1920 => "1080p",
        >= 1280 => "720p",
        _ => $"{w}×{h}",
    };

    public bool IsFavorite
    {
        get => Item.IsFavorite;
        set
        {
            if (Item.IsFavorite == value) return;
            Item.IsFavorite = value;
            _library.SetFavorite(Item.Id, value);
            OnPropertyChanged();
        }
    }

    public void Refresh(WallpaperItem item)
    {
        Item = item;
        OnPropertyChanged(string.Empty);
    }
}
