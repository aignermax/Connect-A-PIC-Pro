using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using Shouldly;

namespace UnitTests.ViewModels;

public class PdkManagerViewModelTests
{
    [Fact]
    public void RegisterPdk_AddsToLoadedPdks()
    {
        var vm = new PdkManagerViewModel();

        vm.RegisterPdk("Test PDK", "/path/pdk.json", false, 10);

        vm.LoadedPdks.Count.ShouldBe(1);
        vm.LoadedPdks[0].Name.ShouldBe("Test PDK");
        vm.LoadedPdks[0].ComponentCount.ShouldBe(10);
    }

    [Fact]
    public void RegisterPdk_UpdatesStatusText()
    {
        var vm = new PdkManagerViewModel();

        vm.RegisterPdk("PDK1", null, true, 5);

        vm.StatusText.ShouldContain("1/1");
    }

    [Fact]
    public void RegisterPdk_MultiplePdks_TracksCount()
    {
        var vm = new PdkManagerViewModel();

        vm.RegisterPdk("PDK1", null, true, 5);
        vm.RegisterPdk("PDK2", "/path/pdk2.json", false, 10);
        vm.RegisterPdk("PDK3", "/path/pdk3.json", false, 15);

        vm.LoadedPdks.Count.ShouldBe(3);
        vm.StatusText.ShouldContain("3/3");
    }

    [Fact]
    public void IsPdkLoaded_ReturnsTrueForLoadedPath()
    {
        var vm = new PdkManagerViewModel();
        var testPath = Path.GetFullPath("/test/pdk.json");
        vm.RegisterPdk("Test", testPath, false, 5);

        var result = vm.IsPdkLoaded(testPath);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsPdkLoaded_ReturnsFalseForUnloadedPath()
    {
        var vm = new PdkManagerViewModel();

        var result = vm.IsPdkLoaded("/not/loaded.json");

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsPdkNameLoaded_ReturnsTrueForLoadedName()
    {
        var vm = new PdkManagerViewModel();
        vm.RegisterPdk("Demo PDK", null, true, 5);

        var result = vm.IsPdkNameLoaded("Demo PDK", null);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsPdkNameLoaded_IsCaseInsensitive()
    {
        var vm = new PdkManagerViewModel();
        vm.RegisterPdk("Demo PDK", null, true, 5);

        var result = vm.IsPdkNameLoaded("DEMO PDK", null);

        result.ShouldBeTrue();
    }

    [Fact]
    public void EnableAll_EnablesAllPdks()
    {
        var vm = new PdkManagerViewModel();
        vm.RegisterPdk("PDK1", null, true, 5);
        vm.RegisterPdk("PDK2", null, true, 10);
        vm.LoadedPdks[0].IsEnabled = false;
        vm.LoadedPdks[1].IsEnabled = false;

        vm.EnableAllCommand.Execute(null);

        vm.LoadedPdks[0].IsEnabled.ShouldBeTrue();
        vm.LoadedPdks[1].IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void DisableAll_DisablesAllPdks()
    {
        var vm = new PdkManagerViewModel();
        vm.RegisterPdk("PDK1", null, true, 5);
        vm.RegisterPdk("PDK2", null, true, 10);

        vm.DisableAllCommand.Execute(null);

        vm.LoadedPdks[0].IsEnabled.ShouldBeFalse();
        vm.LoadedPdks[1].IsEnabled.ShouldBeFalse();
        vm.StatusText.ShouldBe("Disabled 2 PDK(s)");
    }

    [Fact]
    public void GetEnabledPdkNames_ReturnsOnlyEnabledNames()
    {
        var vm = new PdkManagerViewModel();
        vm.RegisterPdk("PDK1", null, true, 5);
        vm.RegisterPdk("PDK2", null, true, 10);
        vm.RegisterPdk("PDK3", null, true, 15);
        vm.LoadedPdks[1].IsEnabled = false;

        var enabled = vm.GetEnabledPdkNames();

        enabled.Count.ShouldBe(2);
        enabled.ShouldContain("PDK1");
        enabled.ShouldNotContain("PDK2");
        enabled.ShouldContain("PDK3");
    }

    [Fact]
    public void UnloadPdk_RemovesUserPdk()
    {
        var vm = new PdkManagerViewModel();
        vm.RegisterPdk("User PDK", "/path/pdk.json", false, 10);

        vm.UnloadPdkCommand.Execute(vm.LoadedPdks[0]);

        vm.LoadedPdks.Count.ShouldBe(0);
    }

    [Fact]
    public void UnloadPdk_DoesNotRemoveBundledPdk()
    {
        var vm = new PdkManagerViewModel();
        vm.RegisterPdk("Bundled PDK", null, true, 5);

        vm.UnloadPdkCommand.Execute(vm.LoadedPdks[0]);

        vm.LoadedPdks.Count.ShouldBe(1);
    }

    [Fact]
    public void OnFilterChanged_InvokedWhenPdkEnabledChanges()
    {
        var vm = new PdkManagerViewModel();
        bool callbackInvoked = false;
        vm.OnFilterChanged = () => callbackInvoked = true;
        vm.RegisterPdk("PDK1", null, true, 5);

        vm.LoadedPdks[0].IsEnabled = false;

        callbackInvoked.ShouldBeTrue();
    }

    [Fact]
    public void RegisterPdk_DesignScopedSetWithoutFile_IsDesignScoped()
    {
        var vm = new PdkManagerViewModel();

        vm.RegisterPdk("GDS Import - test", null, false, 3);

        var pdk = vm.LoadedPdks[0];
        pdk.IsDesignScoped.ShouldBeTrue(
            "a non-bundled PDK without a file lives in the open design (#830) — no edit/delete buttons");
        pdk.SourceBadge.ShouldContain(LocalizationService.Instance.Translate("Pdk.Design"));
    }

    [Fact]
    public void RegisterPdk_FileBackedUserPdk_IsNotDesignScoped()
    {
        var vm = new PdkManagerViewModel();

        vm.RegisterPdk("User PDK", "/path/pdk.json", false, 10);

        vm.LoadedPdks[0].IsDesignScoped.ShouldBeFalse();
    }

    [Fact]
    public void RegisterPdk_BundledPdkWithoutPath_IsNotDesignScoped()
    {
        var vm = new PdkManagerViewModel();

        vm.RegisterPdk("Bundled PDK", null, true, 5);

        vm.LoadedPdks[0].IsDesignScoped.ShouldBeFalse(
            "bundled entries can register without a path but stay bundled, not design-scoped");
    }

    [Fact]
    public void CanDelete_TrueOnlyForFileBackedUserPdks()
    {
        var vm = new PdkManagerViewModel();
        vm.RegisterPdk("User PDK", "/path/pdk.json", false, 10);
        vm.RegisterPdk("Design Set", null, false, 3);
        vm.RegisterPdk("Bundled PDK", null, true, 5);
        vm.RegisterPdk("Bundled With Path", "/path/bundled.json", true, 5);

        vm.LoadedPdks[0].CanDelete.ShouldBeTrue();
        vm.LoadedPdks[1].CanDelete.ShouldBeFalse("design-scoped sets have no file to trash (#830)");
        vm.LoadedPdks[2].CanDelete.ShouldBeFalse("bundled PDKs are read-only");
        vm.LoadedPdks[3].CanDelete.ShouldBeFalse("a path does not make a bundled PDK deletable");
    }
}
