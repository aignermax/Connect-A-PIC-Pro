namespace CAP.Avalonia.Services.Update;

/// <summary>
/// Cross-platform <see cref="IInstaller"/>: replaces the running installation with a downloaded
/// archive and relaunches. Resolves the install location, writes the OS-specific updater script,
/// and hands it to <see cref="DetachedUpdaterLauncher"/> to run after the app quits.
/// </summary>
public class SelfUpdateInstaller : IInstaller
{
    private readonly DetachedUpdaterLauncher _launcher;

    /// <summary>Initializes a new <see cref="SelfUpdateInstaller"/>.</summary>
    public SelfUpdateInstaller(DetachedUpdaterLauncher launcher)
    {
        _launcher = launcher;
    }

    /// <inheritdoc/>
    public bool CanInstallInPlace(out string reason)
    {
        var location = InstallLocation.Resolve();
        if (location is null)
        {
            reason = "the app is not running from an installed location (e.g. launched from source)";
            return false;
        }

        // The macOS bundle swap writes in the parent (moves the .app aside); the Linux file-by-file
        // updater writes INSIDE the root (its backup dir + the replaced files). Require both to be
        // writable so a parent-writable-but-root-read-only layout fails the check up front rather
        // than mid-install.
        var parent = Path.GetDirectoryName(location.Root);
        if (string.IsNullOrEmpty(parent) || !IsDirectoryWritable(parent))
        {
            reason = "the install location is not writable without elevation";
            return false;
        }

        if (Directory.Exists(location.Root) && !IsDirectoryWritable(location.Root))
        {
            reason = "the install directory is not writable without elevation";
            return false;
        }

        // Never treat a shared user directory (home, Downloads, …) as a replaceable install
        // root — updating in place there could delete the user's unrelated files (issue #616).
        // On macOS the root is the .app bundle, which is app-owned, so this never trips there.
        if (InstallLocation.IsSharedUserDirectory(location.Root))
        {
            reason = "the app runs from a shared folder (e.g. Downloads); move it into its own "
                + "folder to enable in-place updates";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <inheritdoc/>
    public void LaunchUpdater(string downloadedArchivePath)
    {
        var location = InstallLocation.Resolve()
            ?? throw new InvalidOperationException("Cannot resolve the install location to update.");

        var script = BuildScript(location, downloadedArchivePath);
        var extension = OperatingSystem.IsWindows() ? ".ps1" : ".sh";
        var scriptPath = Path.Combine(
            Path.GetTempPath(), $"lunima-update-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(scriptPath, script);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _launcher.Launch(scriptPath);
    }

    private static string BuildScript(InstallLocation location, string archivePath)
    {
        if (OperatingSystem.IsWindows()) return UpdaterScripts.BuildWindows(location, archivePath);
        if (OperatingSystem.IsMacOS()) return UpdaterScripts.BuildMacOs(location, archivePath);
        return UpdaterScripts.BuildLinux(location, archivePath);
    }

    /// <summary>Returns true if a file can be created in <paramref name="directory"/> (a write probe).</summary>
    internal static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".lunima-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
