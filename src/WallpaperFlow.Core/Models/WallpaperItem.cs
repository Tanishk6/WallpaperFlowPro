namespace WallpaperFlow.Core.Models;

/// <summary>A wallpaper in the library. Mirrors the <c>wallpapers</c> table.</summary>
public sealed class WallpaperItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public WallpaperType Type { get; set; }

    /// <summary>Absolute path for files, the folder for HTML/shader packages, or the URL for web wallpapers.</summary>
    public string Source { get; set; } = "";

    /// <summary>Entry file inside a package folder (index.html, playlist.m3u) — null for single files.</summary>
    public string? Entry { get; set; }

    public long? FolderId { get; set; }
    public string? Thumbnail { get; set; }
    public string? PreviewClip { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
    public double Fps { get; set; }
    public string? Codec { get; set; }
    public string? Container { get; set; }
    public long Bitrate { get; set; }
    public long FileSize { get; set; }

    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsed { get; set; }
    public int UsageCount { get; set; }

    public bool IsFavorite { get; set; }
    public bool IsInteractive { get; set; }
    public bool IsAudioReactive { get; set; }

    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }

    /// <summary>local | wallpaper-engine | catalog | sync</summary>
    public string Origin { get; set; } = "local";
    public string? OriginId { get; set; }

    public string? ContentHash { get; set; }
    public ulong? PerceptualHash { get; set; }
    public double? QualityScore { get; set; }
    public string? QualityFlags { get; set; }

    /// <summary>User-editable properties (Wallpaper Engine compatible shape), JSON.</summary>
    public string? PropertiesJson { get; set; }
    public EffectsProfile Effects { get; set; } = new();
    public PlaybackOverrides Playback { get; set; } = new();

    public List<string> Tags { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsWeb => Type is WallpaperType.Html or WallpaperType.Url or WallpaperType.Shader or WallpaperType.Clock;
    public bool IsMedia => !IsWeb;
    public string Resolution => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";
    public long PixelCount => (long)Width * Height;

    /// <summary>Path the renderer should open (entry resolved against the source folder).</summary>
    public string ResolveEntryPath() =>
        string.IsNullOrEmpty(Entry) ? Source : Path.Combine(Source, Entry);

    public WallpaperItem Clone()
    {
        var c = (WallpaperItem)MemberwiseClone();
        c.Tags = new List<string>(Tags);
        c.Effects = Effects.Clone();
        c.Playback = Playback with { };
        return c;
    }
}

/// <summary>Per-wallpaper playback overrides. Null means "use the global default".</summary>
public sealed record PlaybackOverrides
{
    public ScalingMode Scaling { get; init; } = ScalingMode.Fill;
    public int Volume { get; init; } = 0;           // 0..100, 0 = muted
    public double Speed { get; init; } = 1.0;       // 0.25..2
    public int? FpsCap { get; init; }               // web wallpapers only
    public double SlideDurationSeconds { get; init; } = 10;
    public bool SlideShuffle { get; init; }
}
