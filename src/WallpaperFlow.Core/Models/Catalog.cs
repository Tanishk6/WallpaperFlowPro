namespace WallpaperFlow.Core.Models;

/// <summary>Online catalog index (https://…/index.json). Designed so a marketplace can serve the same shape.</summary>
public sealed class CatalogIndex
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<CatalogEntry> Items { get; set; } = new();
}

public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public WallpaperType Type { get; set; }
    public string Category { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string ThumbnailUrl { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string License { get; set; } = "";
    /// <summary>Reserved for marketplace integration: 0 = free.</summary>
    public decimal Price { get; set; }
    public string? Currency { get; set; }
}

public sealed class UpdateManifest
{
    public string Version { get; set; } = "0.0.0";
    /// <summary>Direct installer download (verified by <see cref="Sha256"/>). Leave empty to send people to <see cref="Page"/>.</summary>
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>Web page where the new version is downloaded or bought.</summary>
    public string? Page { get; set; }
    public string Notes { get; set; } = "";
    public string? MinimumVersion { get; set; }
    public bool Mandatory { get; set; }
    public DateTime? Published { get; set; }
}
