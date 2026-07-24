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

        vm.PdkChoices.Count.ShouldBe(2);
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
        vm.PdkChoices.Count.ShouldBe(1);

        vm.CreateNewPdk = () =>
        {
            var path = store.CreateNamedPdkWithProcess("Brand New Lib", process, "gdsfactory", null);
            return Task.FromResult<UserPdkInfo?>(new UserPdkInfo("Brand New Lib", path, process));
        };

        vm.SelectedPdkChoice = vm.PdkChoices[0];
        await Task.Yield();

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
        var existingChoice = vm.PdkChoices[0];

        vm.CreateNewPdk = () => Task.FromResult<UserPdkInfo?>(null);

        vm.SelectedPdkChoice = vm.PdkChoices[^1];
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

        vm.SelectedPdkChoice = vm.PdkChoices[^1];
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
        vm.SavedFilePath.ShouldBe(pdkInfo.FilePath);
        var pdk = new PdkLoader().LoadFromFileForEditing(pdkInfo.FilePath);
        pdk.Components.Count.ShouldBe(2);
        pdk.Components.ShouldContain(c => c.Name == "My Comp");
    }

    [Fact]
    public async Task CanSave_isFalse_untilAPdkIsSelected()
    {
        var store = Store();
        var vm = Build(store);

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
