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
    public void BulkCommands_OnlyToggleAllowedPdks_AndLeaveProcessLockedOnesUntouched()
    {
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("Allowed", null, true, 1);
        manager.RegisterPdk("Foreign", null, true, 1);
        manager.ApplyProcessLock(new[] { "Allowed" }); // Foreign → locked+disabled, Allowed → enabled

        // Bulk buttons are always usable now; they operate on the allowed (unlocked) set.
        manager.EnableAllCommand.CanExecute(null).ShouldBeTrue();
        manager.DisableAllCommand.CanExecute(null).ShouldBeTrue();

        manager.DisableAllCommand.Execute(null);
        Pdk(manager, "Allowed").IsEnabled.ShouldBeFalse(); // allowed PDK toggled off
        Pdk(manager, "Foreign").IsEnabled.ShouldBeFalse(); // locked PDK untouched (already off)

        manager.EnableAllCommand.Execute(null);
        Pdk(manager, "Allowed").IsEnabled.ShouldBeTrue();  // allowed PDK enabled
        Pdk(manager, "Foreign").IsEnabled.ShouldBeFalse(); // locked PDK NOT force-enabled

        static PdkInfoViewModel Pdk(PdkManagerViewModel m, string name) =>
            m.LoadedPdks.Single(p => p.Name == name);
    }

    [Fact]
    public void ApplyProcessLock_Reapplied_WithPreserve_KeepsManuallyDisabledMember()
    {
        // Regression for the "save re-shows all hidden PDKs" bug: under a process lock a member
        // PDK stays togglable; a re-apply after a PDK change (preserveMemberToggles) must not
        // force it back on.
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("MemberA", null, true, 1);
        manager.RegisterPdk("MemberB", null, true, 1);
        manager.ApplyProcessLock(new[] { "MemberA", "MemberB" });

        Pdk(manager, "MemberB").IsEnabled = false; // user hides one member

        manager.ApplyProcessLock(new[] { "MemberA", "MemberB" }, preserveMemberToggles: true);

        Pdk(manager, "MemberA").IsEnabled.ShouldBeTrue();
        Pdk(manager, "MemberB").IsEnabled.ShouldBeFalse(); // stays hidden

        static PdkInfoViewModel Pdk(PdkManagerViewModel m, string name) =>
            m.LoadedPdks.Single(p => p.Name == name);
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
