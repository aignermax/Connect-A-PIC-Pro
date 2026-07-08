using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using CAP_Core.Update;

namespace CAP.Avalonia.Services;

/// <summary>
/// Checks GitHub Releases API for newer application versions.
/// Compares versions and returns structured release metadata.
/// </summary>
public class UpdateChecker
{
    private readonly HttpClient _httpClient;
    private readonly string _owner;
    private readonly string _repo;

    /// <summary>
    /// Initializes a new instance of <see cref="UpdateChecker"/>.
    /// </summary>
    /// <param name="httpClient">Configured HTTP client with User-Agent header set.</param>
    /// <param name="owner">GitHub repository owner (e.g. "aignermax").</param>
    /// <param name="repo">GitHub repository name (e.g. "Connect-A-PIC-Pro").</param>
    public UpdateChecker(HttpClient httpClient, string owner, string repo)
    {
        _httpClient = httpClient;
        _owner = owner;
        _repo = repo;
    }

    /// <summary>
    /// Fetches the latest non-prerelease release from GitHub Releases API.
    /// Returns null if the request fails or no release exists.
    /// </summary>
    public async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseReleaseJson(json);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="release"/> has a version newer than <paramref name="currentVersion"/>.
    /// </summary>
    public static bool IsNewerThan(GitHubReleaseInfo release, SemanticVersion currentVersion)
    {
        var releaseVersion = release.ParsedVersion;
        return releaseVersion != null && releaseVersion > currentVersion;
    }

    /// <summary>
    /// Finds the first MSI installer asset in the release's asset list.
    /// Returns null if no MSI asset exists.
    /// </summary>
    /// <remarks>
    /// This method always matches <c>.msi</c> regardless of the current platform.
    /// Use <see cref="FindPlatformAsset"/> when you want platform-aware selection.
    /// </remarks>
    public static GitHubReleaseAsset? FindMsiAsset(GitHubReleaseInfo release)
    {
        return release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the best installer asset for the current operating system.
    /// <list type="bullet">
    ///   <item><description>Windows — <c>.msi</c></description></item>
    ///   <item><description>macOS — <c>.dmg</c>, then <c>.pkg</c></description></item>
    ///   <item><description>Linux — <c>.tar.gz</c>, then <c>.AppImage</c></description></item>
    /// </list>
    /// Returns null if no matching asset exists for the current platform.
    /// </summary>
    public static GitHubReleaseAsset? FindPlatformAsset(GitHubReleaseInfo release)
    {
        if (OperatingSystem.IsWindows())
            return release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

        if (OperatingSystem.IsMacOS())
        {
            // Prefer the .dmg matching the current arch (both osx-arm64 and osx-x64 ship together),
            // then fall back to any .dmg/.pkg so an unusual naming still yields something.
            var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return release.Assets.FirstOrDefault(a =>
                    a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase));
        }

        // Linux
        return release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the release asset used for an <b>in-place auto-update</b> on the current OS/arch:
    /// the arch-specific zipped <c>.app</c> on macOS, the <c>.msi</c> on Windows, the
    /// <c>.tar.gz</c> on Linux. Distinct from <see cref="FindPlatformAsset"/>, which selects the
    /// artifact for a first-time manual install (the macOS <c>.dmg</c>). Returns null when the
    /// release carries no suitable auto-update archive (e.g. an older release without the zip),
    /// so the caller can fall back to the manual installer.
    /// </summary>
    public static GitHubReleaseAsset? FindAutoUpdateAsset(GitHubReleaseInfo release)
    {
        if (OperatingSystem.IsWindows())
            // Windows in-place self-update is deferred (#614 follow-up): msiexec installs to its own
            // perMachine location, so a portable/zip install would be silently misrouted and the
            // stale exe relaunched. Returning null makes the caller fall back to the manual MSI upgrade.
            return null;

        if (OperatingSystem.IsMacOS())
        {
            var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return release.Assets.FirstOrDefault(a =>
                a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        // Linux
        return release.Assets.FirstOrDefault(a =>
            a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses a GitHub Releases API JSON response into a <see cref="GitHubReleaseInfo"/>.
    /// Returns null if parsing fails.
    /// </summary>
    public static GitHubReleaseInfo? ParseReleaseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var name = root.GetProperty("name").GetString() ?? "";
            var body = root.GetProperty("body").GetString() ?? "";
            var isPrerelease = root.GetProperty("prerelease").GetBoolean();
            var publishedAt = root.GetProperty("published_at").GetDateTimeOffset();

            var assets = new List<GitHubReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetsElement))
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    assets.Add(new GitHubReleaseAsset
                    {
                        Name = asset.GetProperty("name").GetString() ?? "",
                        BrowserDownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "",
                        Size = asset.GetProperty("size").GetInt64(),
                        ContentType = asset.GetProperty("content_type").GetString() ?? "",
                    });
                }
            }

            return new GitHubReleaseInfo
            {
                TagName = tagName,
                Name = name,
                Body = body,
                IsPrerelease = isPrerelease,
                PublishedAt = publishedAt,
                Assets = assets,
            };
        }
        catch
        {
            return null;
        }
    }
}
