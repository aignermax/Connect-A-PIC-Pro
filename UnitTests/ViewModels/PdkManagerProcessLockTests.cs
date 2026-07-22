using CAP.Avalonia.ViewModels.Library;
using Shouldly;

namespace UnitTests.ViewModels;

public class PdkManagerProcessLockTests
{
    [Fact]
    public void BulkCommands_OnlyToggleAllowedPdks_AndLeaveProcessLockedOnesUntouched()
    {
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("Allowed", null, true, 1);
        manager.RegisterPdk("Foreign", null, true, 1);
        manager.ApplyProcessLock(new[] { "Allowed" });

        manager.EnableAllCommand.CanExecute(null).ShouldBeTrue();
        manager.DisableAllCommand.CanExecute(null).ShouldBeTrue();

        manager.DisableAllCommand.Execute(null);
        Pdk(manager, "Allowed").IsEnabled.ShouldBeFalse();
        Pdk(manager, "Foreign").IsEnabled.ShouldBeFalse();

        manager.EnableAllCommand.Execute(null);
        Pdk(manager, "Allowed").IsEnabled.ShouldBeTrue();
        Pdk(manager, "Foreign").IsEnabled.ShouldBeFalse();

        static PdkInfoViewModel Pdk(PdkManagerViewModel m, string name) =>
            m.LoadedPdks.Single(p => p.Name == name);
    }

    [Fact]
    public void ApplyProcessLock_Reapplied_WithPreserve_KeepsManuallyDisabledMember()
    {
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("MemberA", null, true, 1);
        manager.RegisterPdk("MemberB", null, true, 1);
        manager.ApplyProcessLock(new[] { "MemberA", "MemberB" });

        Pdk(manager, "MemberB").IsEnabled = false;

        manager.ApplyProcessLock(new[] { "MemberA", "MemberB" }, preserveMemberToggles: true);

        Pdk(manager, "MemberA").IsEnabled.ShouldBeTrue();
        Pdk(manager, "MemberB").IsEnabled.ShouldBeFalse();

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

    [Fact]
    public void GetProcessCompatiblePdkNames_MemberPdkManuallyDisabled_StillCountsAsCompatible()
    {
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("MemberPdk", null, true, 1);
        manager.RegisterPdk("ForeignPdk", null, true, 1);

        manager.ApplyProcessLock(new[] { "MemberPdk" });

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
