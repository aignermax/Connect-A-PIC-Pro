using CAP_Core.Helpers;
using Shouldly;

namespace UnitTests.Helpers;

/// <summary>
/// Tests for <see cref="AppDataFolders"/> — the snap-safe resolution of the
/// per-user application-data root (issue: launching Lunima from a snap-confined
/// VS Code terminal redirected all app data into ~/snap/code/&lt;rev&gt;/…).
/// </summary>
public class AppDataFoldersTests
{
    [Fact]
    public void ResolveLinux_XdgInsideForeignSnap_FallsBackToRealLocalShare()
    {
        var resolved = AppDataFolders.ResolveLinux(
            "/home/max/snap/code/247/.local/share",
            "/home/max");

        resolved.ShouldBe("/home/max/.local/share");
    }

    [Fact]
    public void ResolveLinux_RegularXdgPath_ReturnsUnchanged()
    {
        var resolved = AppDataFolders.ResolveLinux(
            "/home/max/.local/share",
            "/home/max");

        resolved.ShouldBe("/home/max/.local/share");
    }

    [Fact]
    public void ResolveLinux_CustomXdgOutsideSnap_ReturnsUnchanged()
    {
        var resolved = AppDataFolders.ResolveLinux(
            "/mnt/data/xdg-share",
            "/home/max");

        resolved.ShouldBe("/mnt/data/xdg-share");
    }

    [Fact]
    public void ResolveLinux_SnapSiblingPrefixWithoutSeparator_ReturnsUnchanged()
    {
        // "/home/max/snapshots" must NOT be mistaken for "/home/max/snap/…".
        var resolved = AppDataFolders.ResolveLinux(
            "/home/max/snapshots/share",
            "/home/max");

        resolved.ShouldBe("/home/max/snapshots/share");
    }

    [Fact]
    public void ResolveLinux_EmptyInputs_ReturnVerbatim()
    {
        AppDataFolders.ResolveLinux("", "/home/max").ShouldBe("");
        AppDataFolders.ResolveLinux("/x", "").ShouldBe("/x");
    }
}
