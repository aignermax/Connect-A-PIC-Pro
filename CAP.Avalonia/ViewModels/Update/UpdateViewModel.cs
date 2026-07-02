using Avalonia.Controls.ApplicationLifetimes;
using CAP.Avalonia.Services;
using CAP_Core.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Reflection;

namespace CAP.Avalonia.ViewModels.Update;

/// <summary>
/// ViewModel for the software update panel.
/// Handles checking GitHub releases for newer versions, downloading the platform installer,
/// and installing it with graceful application shutdown.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateChecker _updateChecker;
    private readonly UpdateDownloader _downloader;
    private readonly UserPreferencesService _preferences;
    private readonly IUrlLauncher _urlLauncher;
    private readonly SemanticVersion _currentVersion;

    private GitHubReleaseInfo? _availableRelease;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _updateAvailable;

    /// <summary>
    /// Headline shown in the update banner, contrasting both versions once each —
    /// "Update available: v{current} → v{latest}". Avoids the previous redundancy
    /// where the new version was restated in a second status line.
    /// </summary>
    [ObservableProperty]
    private string _latestVersionText = "";

    [ObservableProperty]
    private string _releaseNotes = "";

    /// <summary>Gets the current application version as a display string.</summary>
    public string CurrentVersionText => $"Current: v{_currentVersion}";

    /// <summary>Initializes a new instance of <see cref="UpdateViewModel"/>.</summary>
    public UpdateViewModel(
        UpdateChecker updateChecker,
        UpdateDownloader downloader,
        UserPreferencesService preferences,
        IUrlLauncher urlLauncher)
    {
        _updateChecker = updateChecker;
        _downloader = downloader;
        _preferences = preferences;
        _urlLauncher = urlLauncher;
        _currentVersion = ResolveCurrentVersion();
    }

    /// <summary>
    /// Checks GitHub for a newer release. Updates state to reflect whether
    /// an update is available. Skips versions the user has already dismissed.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsChecking || IsDownloading) return;

        IsChecking = true;
        UpdateAvailable = false;
        StatusText = "Checking for updates...";

        try
        {
            var release = await _updateChecker.GetLatestReleaseAsync();
            if (release == null)
            {
                StatusText = "Could not reach update server. Check your internet connection.";
                return;
            }

            var releaseVersion = release.ParsedVersion;

            if (!UpdateChecker.IsNewerThan(release, _currentVersion))
            {
                StatusText = $"You are up to date! (v{releaseVersion ?? _currentVersion})";
                return;
            }

            // Manual check: always show updates, even if previously skipped
            // (User explicitly wants to check, so honor that intent)
            _availableRelease = release;
            LatestVersionText = $"Update available: v{_currentVersion} → v{releaseVersion}";
            ReleaseNotes = TruncateReleaseNotes(release.Body);
            UpdateAvailable = true;
            // The headline already states the version transition; keep the live status line
            // empty here so it isn't a redundant echo (it carries download progress later).
            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Downloads the platform-appropriate installer from the available release and opens it,
    /// then shuts down the application on Windows (where msiexec replaces the running binary).
    /// On macOS and Linux the app stays open so the user can complete the manual install step.
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_availableRelease == null || IsDownloading) return;

        var platformAsset = UpdateChecker.FindPlatformAsset(_availableRelease);
        if (platformAsset == null)
        {
            // No platform installer found — open GitHub releases page in browser
            StatusText = "Opening GitHub releases page in browser...";
            try
            {
                _urlLauncher.Open(BuildReleaseUrl(_availableRelease.TagName));
            }
            catch (Exception ex)
            {
                StatusText = $"Could not open browser: {ex.Message}";
            }
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        StatusText = "Downloading update...";

        string installerPath;
        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                StatusText = $"Downloading... {p:P0}";
            });

            installerPath = await _downloader.DownloadInstallerAsync(
                platformAsset.BrowserDownloadUrl, platformAsset.Size, progress);
        }
        catch (Exception ex)
        {
            // A failed download has no side effects — the user can simply click Install again.
            StatusText = $"Download failed: {ex.Message}";
            return;
        }
        finally
        {
            IsDownloading = false;
        }

        StatusText = "Download complete. Opening installer...";
        OpenDownloadedInstaller(installerPath);
    }

    /// <summary>
    /// Opens the downloaded installer. When opening fails, the file is already on disk, so the
    /// user is pointed at it (status text + reveal in file manager) instead of being sent to
    /// re-download it from the releases page.
    /// </summary>
    private void OpenDownloadedInstaller(string installerPath)
    {
        try
        {
            _urlLauncher.OpenFileOrDirectory(installerPath);
        }
        catch (Exception)
        {
            StatusText = $"The update was downloaded to {installerPath} but could not be opened "
                + "automatically. Run it from there to finish updating.";
            TryRevealInstaller(installerPath);
            return;
        }
        ShowPostDownloadGuidance();
    }

    private void TryRevealInstaller(string installerPath)
    {
        try
        {
            _urlLauncher.RevealInFileManager(installerPath);
        }
        catch (Exception)
        {
            // The status line already names the full path, so the user can still find the file.
        }
    }

    /// <summary>
    /// Persists this version as skipped so the user is not prompted again.
    /// </summary>
    [RelayCommand]
    private void SkipThisVersion()
    {
        if (_availableRelease?.ParsedVersion == null) return;

        _preferences.SetSkippedUpdateVersion(_availableRelease.ParsedVersion);
        UpdateAvailable = false;
        StatusText = $"Version {_availableRelease.ParsedVersion} will not be shown again.";
        _availableRelease = null;
    }

    /// <summary>
    /// Hides the update panel without skipping — the user will be prompted again next time.
    /// </summary>
    [RelayCommand]
    private void RemindLater()
    {
        UpdateAvailable = false;
        StatusText = "Update available — will remind again next check.";
    }

    /// <summary>
    /// Marks today as skipped so the startup notification is suppressed until tomorrow.
    /// </summary>
    [RelayCommand]
    private void SkipForToday()
    {
        _preferences.SkipToday();
        UpdateAvailable = false;
        StatusText = "Update notification suppressed until tomorrow.";
        _availableRelease = null;
    }

    /// <summary>
    /// Runs on app startup: checks for updates non-blockingly and shows the notification
    /// banner only when an update is available and the user has not skipped today or
    /// permanently skipped this version.
    /// </summary>
    public async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_preferences.ShouldCheckToday()) return;
        if (IsChecking || IsDownloading) return;

        IsChecking = true;
        try
        {
            var release = await _updateChecker.GetLatestReleaseAsync();
            if (release == null) return;
            if (!UpdateChecker.IsNewerThan(release, _currentVersion)) return;

            var skipped = _preferences.GetSkippedUpdateVersion();
            if (skipped != null && release.ParsedVersion != null && skipped >= release.ParsedVersion) return;

            _availableRelease = release;
            LatestVersionText = $"Update available: v{_currentVersion} → v{release.ParsedVersion}";
            ReleaseNotes = TruncateReleaseNotes(release.Body);
            UpdateAvailable = true;
            StatusText = string.Empty;
        }
        catch
        {
            // Startup check failures are silent — don't disturb the user
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// After the installer is opened, either quits (Windows, where msiexec replaces the running
    /// binary) or leaves the app running with platform-specific guidance.
    /// </summary>
    private void ShowPostDownloadGuidance()
    {
        if (OperatingSystem.IsWindows())
        {
            ShutdownApplication();
            return;
        }

        StatusText = BuildPostDownloadGuidance(OperatingSystem.IsMacOS());
    }

    /// <summary>
    /// Guidance shown after the installer was opened on non-Windows platforms. The macOS build
    /// is not yet Apple-notarized, and macOS 15+ no longer offers the right-click → Open
    /// Gatekeeper override, so the unsigned app must be approved via System Settings →
    /// Privacy &amp; Security → "Open Anyway". Approval must happen on the copy in Applications
    /// because the quarantine attribute travels with the app when dragged out of the disk image.
    /// </summary>
    internal static string BuildPostDownloadGuidance(bool isMacOS) =>
        isMacOS
            ? "Update downloaded. Drag Lunima from the disk image to Applications and open it "
              + "once — macOS will block the unsigned app. Allow it under System Settings → "
              + "Privacy & Security → 'Open Anyway' (on macOS 14 and older, right-click the app "
              + "and choose Open instead)."
            : "Update downloaded. Extract the archive and replace your installation to finish updating.";

    private static string BuildReleaseUrl(string tagName) =>
        $"https://github.com/aignermax/Lunima/releases/tag/{tagName}";

    private static SemanticVersion ResolveCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? Assembly.GetExecutingAssembly().GetName().Version;

        if (version == null) return new SemanticVersion(0, 1, 0);
        return new SemanticVersion(version.Major, version.Minor, version.Build);
    }

    private static string TruncateReleaseNotes(string notes)
    {
        const int MaxLength = 800;
        if (notes.Length <= MaxLength) return notes;
        return notes[..MaxLength] + "\n\n[... see full release notes on GitHub]";
    }

    private static void ShutdownApplication()
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
