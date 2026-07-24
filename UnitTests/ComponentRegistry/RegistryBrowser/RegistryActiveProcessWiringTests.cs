using CAP_Core.Components.Process;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.ComponentRegistry;

/// <summary>
/// Verifies that the design's active process (#570) is forwarded to the
/// registry browser (#656) so process-mismatch flagging works in production,
/// not only when tests set <c>ActiveProcessId</c> directly.
/// </summary>
public sealed class RegistryActiveProcessWiringTests
{
    [Fact]
    public void SettingActiveProcess_ForwardsProcessIdToRegistryBrowser()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();

        vm.FileOperations.SetActiveProcess(new ActiveProcessSelection(
            "generic-si220", Fingerprint: null,
            MemberPdkNames: new List<string>(), IsPlayground: false));

        vm.Registry.ActiveProcessId.ShouldBe("generic-si220");
    }

    [Fact]
    public void PlaygroundProcess_ClearsRegistryProcessId()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.FileOperations.SetActiveProcess(new ActiveProcessSelection(
            "generic-si220", Fingerprint: null,
            MemberPdkNames: new List<string>(), IsPlayground: false));

        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.Playground());

        vm.Registry.ActiveProcessId.ShouldBeNull();
    }

    [Fact]
    public void NoActiveProcess_LeavesRegistryProcessIdNull()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();

        vm.Registry.ActiveProcessId.ShouldBeNull();
    }
}
