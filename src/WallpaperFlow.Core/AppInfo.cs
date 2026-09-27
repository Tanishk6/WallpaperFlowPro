namespace WallpaperFlow.Core;

/// <summary>
/// Where this copy of WallpaperFlow Pro lives online. Before publishing, set <see cref="GitHubRepo"/> to your own
/// "username/repository". Update checks read update.json from that repository and stay off until it's set.
/// </summary>
public static class AppInfo
{
    public const string GitHubRepo = "Tanishk6/WallpaperFlowPro";
    public const string CreatorName = "Tnsk.exe";
    public const string CreatorUrl = "https://github.com/Tanishk6";

    public static bool IsConfigured => !GitHubRepo.StartsWith("YOUR-", StringComparison.Ordinal);
    public static string RepoUrl => "https://github.com/" + GitHubRepo;
    public static string IssuesUrl => RepoUrl + "/issues";
    public static string ReleasesUrl => RepoUrl + "/releases/latest";
    public static string UpdateManifestUrl => $"https://raw.githubusercontent.com/{GitHubRepo}/main/update.json";
}
