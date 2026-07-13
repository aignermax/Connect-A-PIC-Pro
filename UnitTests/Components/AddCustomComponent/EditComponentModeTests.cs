using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="NewComponentViewModel.LoadForEdit"/>: prefilling the wizard from an
/// existing custom component's <see cref="ComponentTemplate"/> for in-place editing (name,
/// own-code, backend, fixed target PDK), and that the subsequent <c>Save</c> overwrites the
/// original entry in its named custom PDK file rather than duplicating it. Also covers the
/// <see cref="NewComponentViewModel.WindowTitle"/>/<see cref="NewComponentViewModel.SaveButtonLabel"/>
/// display properties Task 6's view binds to.
/// </summary>
public class EditComponentModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-vm-edit-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 }, new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 } }
    };

    private static PdkComponentDraft SeedComponent(string n, string rawCode) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = rawCode, RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private (NewComponentViewModel vm, Mock<IFdtdSMatrixService> fdtd) Build(
        UserPdkStore store, IReadOnlyList<ProcessDefinition> processes)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        var vm = new NewComponentViewModel(extractor, fdtd.Object, store, processes);
        return (vm, fdtd);
    }

    private (NewComponentViewModel vm, string filePath, string rawCode) BuildWithSeededPdk()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var path = store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        store.AppendToExistingPdk(path, SeedComponent("comp1", rawCode));

        var (vm, _) = Build(store, new List<ProcessDefinition> { process });
        return (vm, path, rawCode);
    }

    private static ComponentTemplate BuildTemplate(string rawCode) => new()
    {
        Name = "comp1",
        RawCode = rawCode,
        RawCodeBackend = "gdsfactory",
        PdkSource = "Lib",
    };

    [Fact]
    public void LoadForEdit_prefillsFieldsAndSelectsThePdk_withoutTriggeringTheNewPdkSentinel()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();
        var createNewPdkCalls = 0;
        vm.CreateNewPdk = () => { createNewPdkCalls++; return Task.FromResult<UserPdkInfo?>(null); };

        vm.LoadForEdit(BuildTemplate(rawCode));

        vm.IsEditMode.ShouldBeTrue();
        vm.ComponentName.ShouldBe("comp1");
        vm.Code.ShouldBe(rawCode);
        vm.SelectedBackend.ShouldBe(GeometryBackend.GdsFactory);
        vm.SelectedCustomPdk.ShouldNotBeNull();
        vm.SelectedCustomPdk!.Name.ShouldBe("Lib");
        createNewPdkCalls.ShouldBe(0); // selecting an existing PDK entry must never invoke the "New PDK…" hook
    }

    [Fact]
    public void WindowTitle_and_SaveButtonLabel_reflectEditMode()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();

        vm.WindowTitle.ShouldBe("New Component");
        vm.SaveButtonLabel.ShouldBe("Save");

        vm.LoadForEdit(BuildTemplate(rawCode));

        vm.WindowTitle.ShouldBe("Edit Component");
        vm.SaveButtonLabel.ShouldBe("Save changes");
    }

    [Fact]
    public async Task Save_afterLoadForEdit_overwritesTheOriginalComponent_inPlace_notDuplicated()
    {
        var (vm, filePath, rawCode) = BuildWithSeededPdk();
        vm.LoadForEdit(BuildTemplate(rawCode));
        vm.ConfirmOverwrite = (_, _) => Task.FromResult(true); // editing intentionally overwrites the same name

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        var pdk = new PdkLoader().LoadFromFileForEditing(filePath);
        pdk.Components.Count(c => c.Name == "comp1").ShouldBe(1); // overwritten, not duplicated
        pdk.Components.Count.ShouldBe(1);
    }

    [Fact]
    public void LoadForEdit_withNoMatchingCustomPdk_reportsStatusAndLeavesEditModeFalse()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();
        var template = BuildTemplate(rawCode);
        template.PdkSource = "Unknown Pdk";

        vm.LoadForEdit(template);

        vm.IsEditMode.ShouldBeFalse();
        vm.StatusText.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LoadForEdit_withNoStoredCode_setsCodeEmpty_andReportsStatus()
    {
        var (vm, _, _) = BuildWithSeededPdk();
        var template = BuildTemplate(null!);
        template.RawCode = null;

        vm.LoadForEdit(template);

        vm.Code.ShouldBe(string.Empty);
        vm.StatusText.ShouldNotBeNullOrWhiteSpace();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
