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
}
