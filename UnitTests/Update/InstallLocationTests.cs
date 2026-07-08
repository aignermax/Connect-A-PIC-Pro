using CAP.Avalonia.Services.Update;
using Shouldly;

namespace UnitTests.Update;

public class InstallLocationTests
{
    [Fact]
    public void FindEnclosingAppBundle_ExecutableInsideBundle_ReturnsBundleRoot()
    {
        // DirectoryInfo.FullName rewrites POSIX paths to drive-rooted backslash paths on
        // Windows, so the literal expectations only hold on POSIX filesystems (where the
        // bundle walk actually runs — macOS at runtime, Linux on CI).
        if (OperatingSystem.IsWindows()) return;

        const string exe = "/Applications/Lunima.app/Contents/MacOS/CAP.Desktop";

        InstallLocation.FindEnclosingAppBundle(exe).ShouldBe("/Applications/Lunima.app");
    }

    [Fact]
    public void FindEnclosingAppBundle_NestedBundlePath_ReturnsNearestBundle()
    {
        if (OperatingSystem.IsWindows()) return;

        const string exe = "/Users/me/Downloads/Lunima.app/Contents/MacOS/CAP.Desktop";

        InstallLocation.FindEnclosingAppBundle(exe).ShouldBe("/Users/me/Downloads/Lunima.app");
    }

    [Fact]
    public void FindEnclosingAppBundle_NotInsideBundle_ReturnsNull()
    {
        const string exe = "/usr/local/bin/Lunima";

        InstallLocation.FindEnclosingAppBundle(exe).ShouldBeNull();
    }

    // ── Shared-directory guard (issue #616 data-loss fix) ──────────────────────
    // The Linux portable build is a loose binary; its "install dir" is wherever the
    // user put it — often a shared folder. Replacing/removing that whole directory
    // would delete unrelated files. These pin the directories the updater must refuse.

    [Fact]
    public void IsSharedUserDirectory_HomeItself_IsTrue()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return;   // CI without a home — skip

        InstallLocation.IsSharedUserDirectory(home).ShouldBeTrue();
    }

    [Fact]
    public void IsSharedUserDirectory_DownloadsUnderHome_IsTrue()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return;

        var downloads = Path.Combine(home, "Downloads");
        InstallLocation.IsSharedUserDirectory(downloads).ShouldBeTrue();
        // Trailing separator must not defeat the check.
        InstallLocation.IsSharedUserDirectory(downloads + Path.DirectorySeparatorChar).ShouldBeTrue();
    }

    [Fact]
    public void IsSharedUserDirectory_TempAndFilesystemRoot_AreTrue()
    {
        InstallLocation.IsSharedUserDirectory(Path.GetTempPath()).ShouldBeTrue();
        InstallLocation.IsSharedUserDirectory(
            Path.GetPathRoot(Environment.CurrentDirectory)!).ShouldBeTrue();
    }

    [Fact]
    public void IsSharedUserDirectory_DedicatedAppFolder_IsFalse()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return;

        // A folder the user created for the app alone is a legitimate install root.
        InstallLocation.IsSharedUserDirectory(Path.Combine(home, "Apps", "Lunima")).ShouldBeFalse();
        InstallLocation.IsSharedUserDirectory(Path.Combine(home, "Downloads", "Lunima")).ShouldBeFalse();
    }
}
