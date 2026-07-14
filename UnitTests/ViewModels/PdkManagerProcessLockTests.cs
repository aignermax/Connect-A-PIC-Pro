using CAP.Avalonia.ViewModels.Library;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Verifies that the bulk Enable All / Disable All commands respect the process lock
/// (issue #570) and that bulk updates batch their filter notifications.
/// </summary>
public class PdkManagerProcessLockTests
{
    [Fact]
    public void BulkCommands_AreDisabled_WhileProcessLockGovernsEnabledSet()
    {
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("Demo", null, true, 3);

        manager.ManualTogglesEnabled = false;

        manager.EnableAllCommand.CanExecute(null).ShouldBeFalse();
        manager.DisableAllCommand.CanExecute(null).ShouldBeFalse();

        manager.ManualTogglesEnabled = true;

        manager.EnableAllCommand.CanExecute(null).ShouldBeTrue();
        manager.DisableAllCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void SetEnabledPdks_BatchesFilterNotifications_ToASingleCallback()
    {
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("A", null, true, 1);
        manager.RegisterPdk("B", null, true, 1);
        manager.RegisterPdk("C", null, true, 1);

        int filterCalls = 0;
        manager.OnFilterChanged = () => filterCalls++;

        manager.SetEnabledPdks(new[] { "A" });

        filterCalls.ShouldBe(1);
        manager.GetEnabledPdkNames().ShouldBe(new[] { "A" }, ignoreOrder: true);
    }

    /// <summary>
    /// Regression test for the design-check false positive: deselecting a member PDK's
    /// library-filter checkbox is a filtering choice (<see cref="PdkManagerViewModel.ApplyProcessLock"/>
    /// doc), not a process violation, so its already-placed components must not become
    /// "process conflicted". <see cref="PdkManagerViewModel.GetEnabledPdkNames"/> alone cannot
    /// tell that apart from a real foreign-process PDK — only <c>IsLockedByProcess</c> can — so
    /// design-check wiring must consult <see cref="PdkManagerViewModel.GetProcessCompatiblePdkNames"/>
    /// instead.
    /// </summary>
    [Fact]
    public void GetProcessCompatiblePdkNames_MemberPdkManuallyDisabled_StillCountsAsCompatible()
    {
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("MemberPdk", null, true, 1);
        manager.RegisterPdk("ForeignPdk", null, true, 1);

        manager.ApplyProcessLock(new[] { "MemberPdk" });

        // User declutters the library by unchecking the member PDK.
        manager.LoadedPdks.First(p => p.Name == "MemberPdk").IsEnabled = false;

        manager.GetEnabledPdkNames().ShouldNotContain("MemberPdk");
        manager.GetProcessCompatiblePdkNames().ShouldContain("MemberPdk");
        manager.GetProcessCompatiblePdkNames().ShouldNotContain("ForeignPdk");
    }

    [Fact]
    public void GetProcessCompatiblePdkNames_NoActiveProcess_ReturnsAllLoadedPdks()
    {
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("A", null, true, 1);
        manager.RegisterPdk("B", null, true, 1);

        manager.GetProcessCompatiblePdkNames().ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
    }
}
