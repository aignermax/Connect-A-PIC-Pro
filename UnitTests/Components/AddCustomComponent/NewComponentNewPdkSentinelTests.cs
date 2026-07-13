using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers the "New PDK…" dropdown sentinel that replaces the old inline new-PDK/process UI
/// (task 4 of the PDK-first component wizard, #723/#727 follow-up):
/// <see cref="NewComponentViewModel.PdkChoices"/> always ends with the sentinel, selecting it
/// invokes <see cref="NewComponentViewModel.CreateNewPdk"/> and adopts a successful result,
/// a cancelled (null) creation reverts to the previously selected PDK, and saving against an
/// existing PDK always appends to that PDK's own file (never a new "SaveToNamedPdk" branch).
/// </summary>
public class NewComponentNewPdkSentinelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-nc-vm-sentinel-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 },
            new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 }
        }
    };

    private static PdkComponentDraft SeedComponent(string n) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private NewComponentViewModel Build(UserPdkStore store)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var vm = new NewComponentViewModel(extractor, fdtd: null, store,
            new List<ProcessDefinition> { new() { Name = "P" } });
        vm.ComponentName = "My Comp";
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        return vm;
    }

    [Fact]
    public void PdkChoices_alwaysEndsWithTheNewPdkSentinel()
    {
        var store = Store();
        store.SaveToNamedPdk("Lib A", new ProcessDefinition { Name = "P" }, SeedComponent("x"), "gdsfactory", null);
        var vm = Build(store);

        vm.PdkChoices.Count.ShouldBe(2); // one existing PDK + the sentinel
        vm.PdkChoices[^1].IsNewPdk.ShouldBeTrue();
        vm.PdkChoices[^1].DisplayName.ShouldBe("New PDK…");
        vm.PdkChoices[^1].Pdk.ShouldBeNull();
    }

    [Fact]
    public async Task SelectingTheSentinel_withACreatedPdk_adoptsItAsTheSelection()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        var vm = Build(store);
        vm.PdkChoices.Count.ShouldBe(1); // no custom PDKs yet -> only the sentinel

        vm.CreateNewPdk = () =>
        {
            var path = store.CreateNamedPdkWithProcess("Brand New Lib", process, "gdsfactory", null);
            return Task.FromResult<UserPdkInfo?>(new UserPdkInfo("Brand New Lib", path, process));
        };

        vm.SelectedPdkChoice = vm.PdkChoices[0]; // the sentinel (only entry)
        await Task.Yield(); // let the fire-and-forget creation task settle

        vm.SelectedCustomPdk.ShouldNotBeNull();
        vm.SelectedCustomPdk!.Name.ShouldBe("Brand New Lib");
        vm.AvailableCustomPdks.ShouldContain(i => i.Name == "Brand New Lib");
        vm.PdkChoices.ShouldContain(c => !c.IsNewPdk && c.Pdk!.Name == "Brand New Lib");
    }

    [Fact]
    public async Task SelectingTheSentinel_thenCancelling_revertsToThePreviousSelection()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("Existing Lib", process, SeedComponent("x"), "gdsfactory", null);
        var vm = Build(store);
        var existingChoice = vm.PdkChoices[0]; // ctor already pre-selected this one

        vm.CreateNewPdk = () => Task.FromResult<UserPdkInfo?>(null); // user cancels the modal

        vm.SelectedPdkChoice = vm.PdkChoices[^1]; // the sentinel
        await Task.Yield();

        vm.SelectedPdkChoice.ShouldBe(existingChoice);
        vm.SelectedCustomPdk!.Name.ShouldBe("Existing Lib");
    }

    [Fact]
    public async Task SelectingTheSentinel_withNoCreateHookWired_revertsImmediately()
    {
        var store = Store();
        store.SaveToNamedPdk("Existing Lib", new ProcessDefinition { Name = "P" }, SeedComponent("x"), "gdsfactory", null);
        var vm = Build(store);
        var existingChoice = vm.PdkChoices[0];

        vm.SelectedPdkChoice = vm.PdkChoices[^1]; // sentinel, but CreateNewPdk was never set
        await Task.Yield();

        vm.SelectedPdkChoice.ShouldBe(existingChoice);
    }

    [Fact]
    public async Task Save_withAnExistingSelectedPdk_appendsToItsOwnFile()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        store.SaveToNamedPdk("My SiN Lib", process, SeedComponent("existing"), "gdsfactory", null);
        var vm = Build(store);
        var pdkInfo = vm.AvailableCustomPdks.ShouldHaveSingleItem();
        vm.SelectedPdkChoice = vm.PdkChoices[0];

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedFilePath.ShouldBe(pdkInfo.FilePath); // appended to the selected PDK's own file
        var pdk = new PdkLoader().LoadFromFileForEditing(pdkInfo.FilePath);
        pdk.Components.Count.ShouldBe(2); // appended, not replacing the file
        pdk.Components.ShouldContain(c => c.Name == "My Comp");
    }

    [Fact]
    public async Task CanSave_isFalse_untilAPdkIsSelected()
    {
        var store = Store();
        var vm = Build(store); // no custom PDKs -> nothing selected

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.HasPreview.ShouldBeTrue();

        vm.SelectedCustomPdk.ShouldBeNull();
        vm.SaveCommand.CanExecute(null).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
