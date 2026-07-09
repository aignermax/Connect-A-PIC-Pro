namespace CAP.Avalonia.Services.Update;

/// <summary>
/// Installs a downloaded update over the current installation and relaunches the app.
/// The download itself is handled by <see cref="UpdateDownloader"/>; this seam owns only the
/// "replace the installed app and restart" step, which differs per operating system.
/// </summary>
public interface IInstaller
{
    /// <summary>
    /// True when an in-place update can be performed right now — the running app's install
    /// location is known and writable. When false, <paramref name="reason"/> explains why and the
    /// caller should fall back to a manual installer / the releases page instead of quitting.
    /// </summary>
    bool CanInstallInPlace(out string reason);

    /// <summary>
    /// Writes and launches a DETACHED updater that waits for this process to exit, replaces the
    /// installed application with the contents of <paramref name="downloadedArchivePath"/>, and
    /// relaunches the new version. After this returns the caller MUST quit the app so the updater
    /// can replace files and relaunch.
    /// </summary>
    void LaunchUpdater(string downloadedArchivePath);
}
