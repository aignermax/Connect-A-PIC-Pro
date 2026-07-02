using System.Net;
using System.Net.Http;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Update;
using CAP_Core.Update;
using Shouldly;

namespace UnitTests.Update;

/// <summary>
/// Integration tests for UpdateViewModel — combines UpdateChecker (with mocked HTTP)
/// and UpdateDownloader, exercising the full check → UI state flow.
/// </summary>
public class UpdateViewModelTests
{
    private const string NewerReleaseJson = """
        {
          "tag_name": "v99.0.0",
          "name": "Version 99.0.0",
          "body": "Major update with improvements.",
          "prerelease": false,
          "published_at": "2099-01-01T00:00:00Z",
          "assets": [
            {
              "name": "Lunima-99.0.0.msi",
              "browser_download_url": "https://example.com/Lunima-99.0.0.msi",
              "size": 1024,
              "content_type": "application/x-msi"
            }
          ]
        }
        """;

    private const string OlderReleaseJson = """
        {
          "tag_name": "v0.0.1",
          "name": "Version 0.0.1",
          "body": "Initial release.",
          "prerelease": false,
          "published_at": "2020-01-01T00:00:00Z",
          "assets": []
        }
        """;

    /// <summary>Release carrying an installer for every OS, so FindPlatformAsset resolves on any runner.</summary>
    private const string AllPlatformsReleaseJson = """
        {
          "tag_name": "v99.0.0",
          "name": "Version 99.0.0",
          "body": "Cross-platform release.",
          "prerelease": false,
          "published_at": "2099-01-01T00:00:00Z",
          "assets": [
            { "name": "Lunima-99.0.0.msi",    "browser_download_url": "https://example.com/Lunima-99.0.0.msi",    "size": 8, "content_type": "application/x-msi" },
            { "name": "Lunima-99.0.0.dmg",    "browser_download_url": "https://example.com/Lunima-99.0.0.dmg",    "size": 8, "content_type": "application/octet-stream" },
            { "name": "Lunima-99.0.0.tar.gz", "browser_download_url": "https://example.com/Lunima-99.0.0.tar.gz", "size": 8, "content_type": "application/gzip" }
          ]
        }
        """;

    private static UpdateViewModel CreateViewModel(
        string responseJson,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? tempPrefsPath = null,
        IUrlLauncher? urlLauncher = null)
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(statusCode)
            {
                Content = statusCode == HttpStatusCode.OK
                    ? new StringContent(responseJson)
                    : new StringContent("")
            });
        var httpClient = new HttpClient(handler);
        var checker = new UpdateChecker(httpClient, "owner", "repo");
        var downloader = new UpdateDownloader(httpClient);
        var prefs = tempPrefsPath != null
            ? new UserPreferencesService(tempPrefsPath)
            : new UserPreferencesService(Path.GetTempFileName());
        return new UpdateViewModel(checker, downloader, prefs, urlLauncher ?? new FakeUrlLauncher());
    }

    private static UpdateViewModel CreateViewModel(HttpMessageHandler handler, IUrlLauncher urlLauncher)
    {
        var httpClient = new HttpClient(handler);
        return new UpdateViewModel(
            new UpdateChecker(httpClient, "owner", "repo"),
            new UpdateDownloader(httpClient),
            new UserPreferencesService(Path.GetTempFileName()),
            urlLauncher);
    }

    [Fact]
    public async Task CheckForUpdates_NewerVersionExists_SetsUpdateAvailableTrue()
    {
        var vm = CreateViewModel(NewerReleaseJson);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.UpdateAvailable.ShouldBeTrue();
        vm.LatestVersionText.ShouldContain("99");
        vm.ReleaseNotes.ShouldContain("Major update");
    }

    [Fact]
    public async Task CheckForUpdates_Available_HeadlineShowsBothVersions_NoRedundantStatusEcho()
    {
        // Banner clarity fix: a single headline contrasts current → latest (each version once),
        // and the live status line is not a redundant restatement of the same version.
        var vm = CreateViewModel(NewerReleaseJson);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.UpdateAvailable.ShouldBeTrue();
        vm.LatestVersionText.ShouldStartWith("Update available: v");
        vm.LatestVersionText.ShouldContain("→");      // both versions, contrasted
        vm.LatestVersionText.ShouldContain("99.0.0"); // the latest (online) version
        vm.StatusText.ShouldBeNullOrEmpty();          // no redundant version echo
    }

    [Fact]
    public async Task CheckForUpdates_CurrentIsLatest_UpdateAvailableIsFalse()
    {
        var vm = CreateViewModel(OlderReleaseJson);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.UpdateAvailable.ShouldBeFalse();
        vm.StatusText.ShouldContain("up to date");
    }

    [Fact]
    public async Task CheckForUpdates_HttpFailure_ShowsErrorStatus()
    {
        var vm = CreateViewModel("", HttpStatusCode.ServiceUnavailable);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.UpdateAvailable.ShouldBeFalse();
        vm.StatusText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckForUpdates_SkippedVersionMatches_StillShowsUpdateForManualCheck()
    {
        // Manual check always shows updates, even for previously skipped versions.
        // Only auto-startup checks should respect the skip preference.
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            prefs.SetSkippedUpdateVersion(new SemanticVersion(99, 0, 0));

            var handler = new FakeHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(NewerReleaseJson)
                });
            var httpClient = new HttpClient(handler);
            var checker = new UpdateChecker(httpClient, "owner", "repo");
            var downloader = new UpdateDownloader(httpClient);
            var vm = new UpdateViewModel(checker, downloader, prefs, new FakeUrlLauncher());

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            vm.UpdateAvailable.ShouldBeTrue();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task SkipThisVersion_PersistsVersionAndHidesPanel()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            var handler = new FakeHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(NewerReleaseJson)
                });
            var httpClient = new HttpClient(handler);
            var vm = new UpdateViewModel(
                new UpdateChecker(httpClient, "o", "r"), new UpdateDownloader(httpClient), prefs, new FakeUrlLauncher());

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            vm.UpdateAvailable.ShouldBeTrue();

            vm.SkipThisVersionCommand.Execute(null);

            vm.UpdateAvailable.ShouldBeFalse();
            var skipped = prefs.GetSkippedUpdateVersion();
            skipped.ShouldNotBeNull();
            skipped!.Major.ShouldBe(99);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task RemindLater_HidesPanelWithoutPersisting()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            var handler = new FakeHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(NewerReleaseJson)
                });
            var httpClient = new HttpClient(handler);
            var vm = new UpdateViewModel(
                new UpdateChecker(httpClient, "o", "r"), new UpdateDownloader(httpClient), prefs, new FakeUrlLauncher());

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            vm.UpdateAvailable.ShouldBeTrue();

            vm.RemindLaterCommand.Execute(null);

            vm.UpdateAvailable.ShouldBeFalse();
            prefs.GetSkippedUpdateVersion().ShouldBeNull();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void CurrentVersionText_ContainsVersionPrefix()
    {
        var vm = CreateViewModel(OlderReleaseJson);
        vm.CurrentVersionText.ShouldStartWith("Current: v");
    }

    [Fact]
    public async Task InstallUpdate_NoMsiAsset_ShowsErrorMessage()
    {
        var noMsiJson = """
            {
              "tag_name": "v99.0.0",
              "name": "Version 99.0.0",
              "body": "No MSI this time.",
              "prerelease": false,
              "published_at": "2099-01-01T00:00:00Z",
              "assets": []
            }
            """;

        var urlLauncher = new FakeUrlLauncher();
        var vm = CreateViewModel(noMsiJson, urlLauncher: urlLauncher);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        vm.UpdateAvailable.ShouldBeTrue();

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("GitHub releases page");
        urlLauncher.LastOpenedUrl.ShouldBe("https://github.com/aignermax/Lunima/releases/tag/v99.0.0");
    }

    [Fact]
    public async Task InstallUpdate_PlatformAssetPresent_OpensInstallerWithGuidanceAndKeepsAppAliveOnNonWindows()
    {
        // #613: after downloading, the installer is opened via the launcher and the user gets
        // actionable guidance (per-platform wording is covered by the BuildPostDownloadGuidance
        // tests below), so the update banner never dead-ends.
        // A repeating handler is required: this exercises TWO requests (metadata + download),
        // and a single shared response can only be read once.
        var launcher = new FakeUrlLauncher();
        var vm = CreateViewModel(StubHttpMessageHandler.RepeatingOk(AllPlatformsReleaseJson), launcher);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        vm.UpdateAvailable.ShouldBeTrue();

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        try
        {
            launcher.LastOpenedPath.ShouldNotBeNull();   // installer opened through the abstraction
            vm.StatusText.ShouldNotBeNullOrEmpty();       // user is never left without direction

            if (OperatingSystem.IsMacOS())
                vm.StatusText.ShouldContain("Open Anyway");
            else if (!OperatingSystem.IsWindows())
                vm.StatusText.ShouldContain("Extract the archive");
        }
        finally
        {
            if (launcher.LastOpenedPath != null && File.Exists(launcher.LastOpenedPath))
                File.Delete(launcher.LastOpenedPath);
        }
    }

    [Fact]
    public async Task InstallUpdate_DownloadFails_ShowsRetryableErrorWithoutOpeningBrowser()
    {
        // A failed download has no side effects — the user can simply click Install again.
        // In particular it must not hijack focus with a browser tab on every (possibly
        // transient) network error.
        var launcher = new FakeUrlLauncher();
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.Host == "example.com"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AllPlatformsReleaseJson) });
        var vm = CreateViewModel(handler, launcher);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        vm.UpdateAvailable.ShouldBeTrue();

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("Download failed");
        launcher.LastOpenedUrl.ShouldBeNull();    // no browser fallback for a retryable failure
        launcher.LastOpenedPath.ShouldBeNull();   // nothing was opened
        vm.IsDownloading.ShouldBeFalse();         // the Install button is usable again
    }

    [Fact]
    public async Task InstallUpdate_OpeningInstallerFails_RevealsDownloadedFileInsteadOfClaimingDownloadFailed()
    {
        // The download succeeded; only opening the file failed. The status must not claim
        // "Download failed", and instead of sending the user to re-download ~100MB from the
        // releases page, the already-downloaded installer is revealed in the file manager.
        var launcher = new FakeUrlLauncher { ThrowOnOpenFileOrDirectory = true };
        var vm = CreateViewModel(StubHttpMessageHandler.RepeatingOk(AllPlatformsReleaseJson), launcher);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        vm.UpdateAvailable.ShouldBeTrue();

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        try
        {
            vm.StatusText.ShouldNotContain("Download failed");
            launcher.LastRevealedPath.ShouldNotBeNull();          // user is pointed at the file on disk
            vm.StatusText.ShouldContain(launcher.LastRevealedPath!);
            launcher.LastOpenedUrl.ShouldBeNull();                // no browser detour to re-download
        }
        finally
        {
            if (launcher.LastRevealedPath != null && File.Exists(launcher.LastRevealedPath))
                File.Delete(launcher.LastRevealedPath);
        }
    }

    [Fact]
    public void PostDownloadGuidance_MacOS_PointsToSystemSettingsOpenAnyway()
    {
        // macOS 15 (Sequoia) removed the Control-click → Open Gatekeeper override for
        // unsigned apps; the surviving path is System Settings → Privacy & Security →
        // "Open Anyway", and approval must happen on the copy in /Applications (the
        // quarantine attribute travels with the app when it is dragged out of the dmg).
        var guidance = UpdateViewModel.BuildPostDownloadGuidance(isMacOS: true);

        guidance.ShouldContain("Applications");
        guidance.ShouldContain("Open Anyway");
        guidance.ShouldContain("right-click");   // still works on macOS 14 and older
    }

    [Fact]
    public void PostDownloadGuidance_NonMacOS_TellsUserToExtractArchive()
    {
        UpdateViewModel.BuildPostDownloadGuidance(isMacOS: false)
            .ShouldContain("Extract the archive");
    }

    private sealed class FakeUrlLauncher : IUrlLauncher
    {
        public string? LastOpenedUrl { get; private set; }
        public string? LastOpenedPath { get; private set; }
        public string? LastRevealedPath { get; private set; }
        public bool ThrowOnOpenFileOrDirectory { get; init; }

        public void Open(string url)
        {
            LastOpenedUrl = url;
        }

        public void OpenFileOrDirectory(string path)
        {
            if (ThrowOnOpenFileOrDirectory)
                throw new InvalidOperationException($"No application is associated with {path}.");
            LastOpenedPath = path;
        }

        public void RevealInFileManager(string path)
        {
            LastRevealedPath = path;
        }
    }

    // --- Skip for Today tests ---

    [Fact]
    public async Task StartupCheck_SkipForTodaySet_DoesNotShowNotification()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            prefs.SkipToday();

            var vm = CreateViewModelWithPrefs(NewerReleaseJson, prefs);

            await vm.CheckForUpdatesOnStartupAsync();

            vm.UpdateAvailable.ShouldBeFalse();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task StartupCheck_SkipForTodayNotSet_ShowsNotification()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            // No skip set — should check today
            var vm = CreateViewModelWithPrefs(NewerReleaseJson, prefs);

            await vm.CheckForUpdatesOnStartupAsync();

            vm.UpdateAvailable.ShouldBeTrue();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task StartupCheck_SkippedVersionMatchesRelease_DoesNotShowNotification()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            prefs.SetSkippedUpdateVersion(new SemanticVersion(99, 0, 0));

            var vm = CreateViewModelWithPrefs(NewerReleaseJson, prefs);

            await vm.CheckForUpdatesOnStartupAsync();

            vm.UpdateAvailable.ShouldBeFalse();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ManualCheck_AlwaysShowsUpdate_EvenWhenSkippedToday()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            prefs.SkipToday();

            var vm = CreateViewModelWithPrefs(NewerReleaseJson, prefs);

            // Manual check ignores skip-today
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            vm.UpdateAvailable.ShouldBeTrue();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void SkipForToday_HidesPanelAndPersistsTodayDate()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            var vm = CreateViewModelWithPrefs(NewerReleaseJson, prefs);

            vm.SkipForTodayCommand.Execute(null);

            vm.UpdateAvailable.ShouldBeFalse();
            prefs.ShouldCheckToday().ShouldBeFalse();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ShouldCheckToday_AfterSkipToday_ReturnsFalse()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            prefs.SkipToday();
            prefs.ShouldCheckToday().ShouldBeFalse();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ShouldCheckToday_WhenNeverSkipped_ReturnsTrue()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var prefs = new UserPreferencesService(tempPath);
            prefs.ShouldCheckToday().ShouldBeTrue();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static UpdateViewModel CreateViewModelWithPrefs(string responseJson, UserPreferencesService prefs)
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            });
        var httpClient = new HttpClient(handler);
        return new UpdateViewModel(
            new UpdateChecker(httpClient, "owner", "repo"),
            new UpdateDownloader(httpClient),
            prefs,
            new FakeUrlLauncher());
    }

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

    /// <summary>Builds a fresh response per request via a delegate, so a body can be consumed
    /// by more than one call (metadata fetch followed by installer download) and responses can
    /// differ per URL (e.g. metadata succeeds while the asset download fails).</summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public static StubHttpMessageHandler RepeatingOk(string body) =>
            new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_respond(request));
        }
    }
}
