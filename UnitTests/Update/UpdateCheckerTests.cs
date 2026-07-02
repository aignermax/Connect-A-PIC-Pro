using System.Net;
using System.Net.Http;
using CAP.Avalonia.Services;
using CAP_Core.Update;
using Shouldly;

namespace UnitTests.Update;

public class UpdateCheckerTests
{
    private const string SampleReleaseJson = """
        {
          "tag_name": "v1.5.0",
          "name": "Version 1.5.0",
          "body": "## What's new\n- Feature A\n- Bug fix B",
          "prerelease": false,
          "published_at": "2025-01-15T12:00:00Z",
          "assets": [
            {
              "name": "Lunima-1.5.0.msi",
              "browser_download_url": "https://github.com/aignermax/Connect-A-PIC-Pro/releases/download/v1.5.0/Lunima-1.5.0.msi",
              "size": 10485760,
              "content_type": "application/x-msi"
            },
            {
              "name": "Lunima-1.5.0-portable.zip",
              "browser_download_url": "https://github.com/aignermax/Connect-A-PIC-Pro/releases/download/v1.5.0/Lunima-1.5.0-portable.zip",
              "size": 5242880,
              "content_type": "application/zip"
            }
          ]
        }
        """;

    [Fact]
    public void ParseReleaseJson_ValidJson_ReturnsCorrectRelease()
    {
        var release = UpdateChecker.ParseReleaseJson(SampleReleaseJson);

        release.ShouldNotBeNull();
        release.TagName.ShouldBe("v1.5.0");
        release.Name.ShouldBe("Version 1.5.0");
        release.IsPrerelease.ShouldBeFalse();
        release.Body.ShouldContain("Feature A");
        release.Assets.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseReleaseJson_ParsedVersion_MatchesTag()
    {
        var release = UpdateChecker.ParseReleaseJson(SampleReleaseJson)!;

        var version = release.ParsedVersion;
        version.ShouldNotBeNull();
        version!.Major.ShouldBe(1);
        version.Minor.ShouldBe(5);
        version.Patch.ShouldBe(0);
    }

    [Fact]
    public void ParseReleaseJson_InvalidJson_ReturnsNull()
    {
        var result = UpdateChecker.ParseReleaseJson("{ not valid json }");
        result.ShouldBeNull();
    }

    [Fact]
    public void ParseReleaseJson_EmptyString_ReturnsNull()
    {
        var result = UpdateChecker.ParseReleaseJson("");
        result.ShouldBeNull();
    }

    [Fact]
    public void FindMsiAsset_ReleaseWithMsi_ReturnsMsiAsset()
    {
        var release = UpdateChecker.ParseReleaseJson(SampleReleaseJson)!;

        var msiAsset = UpdateChecker.FindMsiAsset(release);

        msiAsset.ShouldNotBeNull();
        msiAsset!.Name.ShouldBe("Lunima-1.5.0.msi");
    }

    [Fact]
    public void FindMsiAsset_ReleaseWithoutMsi_ReturnsNull()
    {
        var release = new GitHubReleaseInfo
        {
            TagName = "v1.0.0",
            Assets = new List<GitHubReleaseAsset>
            {
                new() { Name = "source.zip", BrowserDownloadUrl = "https://example.com/source.zip" }
            }
        };

        UpdateChecker.FindMsiAsset(release).ShouldBeNull();
    }

    [Fact]
    public void FindPlatformAsset_AllPlatformAssetsPresent_ReturnsCorrectAssetForCurrentOS()
    {
        var release = new GitHubReleaseInfo
        {
            TagName = "v1.5.0",
            Assets = new List<GitHubReleaseAsset>
            {
                new() { Name = "Lunima-1.5.0.msi",    BrowserDownloadUrl = "https://example.com/Lunima.msi" },
                new() { Name = "Lunima-1.5.0.dmg",    BrowserDownloadUrl = "https://example.com/Lunima.dmg" },
                new() { Name = "Lunima-1.5.0.tar.gz", BrowserDownloadUrl = "https://example.com/Lunima.tar.gz" },
            }
        };

        var asset = UpdateChecker.FindPlatformAsset(release);

        asset.ShouldNotBeNull();
        if (OperatingSystem.IsWindows())
            asset!.Name.ShouldEndWith(".msi");
        else if (OperatingSystem.IsMacOS())
            asset!.Name.ShouldEndWith(".dmg");
        else
            asset!.Name.ShouldEndWith(".tar.gz");
    }

    [Fact]
    public void FindPlatformAsset_EmptyAssets_ReturnsNull()
    {
        var release = new GitHubReleaseInfo
        {
            TagName = "v1.0.0",
            Assets = new List<GitHubReleaseAsset>()
        };

        UpdateChecker.FindPlatformAsset(release).ShouldBeNull();
    }

    [Fact]
    public void FindPlatformAsset_OnlyMsiAvailable_ReturnsNullOnNonWindows()
    {
        // Regression test: FindPlatformAsset must NOT return a .msi on macOS/Linux.
        // Root cause of the macOS "downloads .msi instead of .dmg" bug (issue #610):
        // the ViewModel was calling FindMsiAsset (always .msi) instead of FindPlatformAsset.
        var release = new GitHubReleaseInfo
        {
            TagName = "v1.0.0",
            Assets = new List<GitHubReleaseAsset>
            {
                new() { Name = "Lunima-1.0.0.msi", BrowserDownloadUrl = "https://example.com/Lunima.msi" },
            }
        };

        if (OperatingSystem.IsWindows())
            UpdateChecker.FindPlatformAsset(release).ShouldNotBeNull();
        else
            UpdateChecker.FindPlatformAsset(release).ShouldBeNull();
    }

    [Theory]
    [InlineData("v1.5.0", "1.4.0", true)]   // release newer
    [InlineData("v1.5.0", "1.5.0", false)]  // same version
    [InlineData("v1.5.0", "2.0.0", false)]  // current is newer
    [InlineData("v0.9.0", "1.0.0", false)]  // older release
    public void IsNewerThan_VariousVersions_ReturnsExpectedResult(
        string releaseTag, string currentVersionStr, bool expectedNewer)
    {
        var release = new GitHubReleaseInfo { TagName = releaseTag };
        var currentVersion = SemanticVersion.Parse(currentVersionStr);

        UpdateChecker.IsNewerThan(release, currentVersion).ShouldBe(expectedNewer);
    }

    [Fact]
    public void IsNewerThan_InvalidTag_ReturnsFalse()
    {
        var release = new GitHubReleaseInfo { TagName = "not-a-version" };
        var currentVersion = new SemanticVersion(1, 0, 0);

        UpdateChecker.IsNewerThan(release, currentVersion).ShouldBeFalse();
    }

    [Fact]
    public async Task GetLatestReleaseAsync_HttpError_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var httpClient = new HttpClient(handler);
        var checker = new UpdateChecker(httpClient, "owner", "repo");

        var result = await checker.GetLatestReleaseAsync();

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ValidResponse_ParsesRelease()
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleReleaseJson)
            });
        var httpClient = new HttpClient(handler);
        var checker = new UpdateChecker(httpClient, "owner", "repo");

        var result = await checker.GetLatestReleaseAsync();

        result.ShouldNotBeNull();
        result!.TagName.ShouldBe("v1.5.0");
    }

    /// <summary>Minimal HttpMessageHandler for test isolation.</summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FakeHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
