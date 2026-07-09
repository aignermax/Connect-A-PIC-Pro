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
}
