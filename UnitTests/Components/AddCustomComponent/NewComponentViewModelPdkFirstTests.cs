using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
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
/// Covers the PDK-first redesign of <see cref="NewComponentViewModel"/>: choosing an existing
/// named custom PDK (<see cref="NewComponentViewModel.SelectedCustomPdk"/>, process inherited and
/// read-only) via <see cref="NewComponentViewModel.SelectedPdkChoice"/>, and routing
/// <c>Save</c> to <see cref="UserPdkStore.AppendToExistingPdk"/>. A brand-new PDK is never
/// created inline anymore — see <c>NewComponentNewPdkSentinelTests</c> for the "New PDK…"
/// dropdown sentinel that replaced it (task 4 of the PDK-first component wizard). Function-
/// reference mode no longer exists either — every save is the user's own code.
/// </summary>
public class NewComponentViewModelPdkFirstTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-vm-pdkfirst-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 }, new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 } }
    };

    private static PdkComponentDraft SeedComponent(string n) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private (NewComponentViewModel vm, Mock<IFdtdSMatrixService> fdtd) Build(
        UserPdkStore store, IReadOnlyList<ProcessDefinition>? processes = null, bool withFdtd = true)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        var vm = new NewComponentViewModel(extractor, withFdtd ? fdtd.Object : null, store,
            processes ?? new List<ProcessDefinition> { new() { Name = "P" } });
        vm.ComponentName = "My Comp";
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        return (vm, fdtd);
    }

    [Fact]
    public async Task ExistingCustomPdkSelected_inheritsProcess_andAppendsToItsFile()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        store.SaveToNamedPdk("My SiN Lib", process, SeedComponent("existing"), "gdsfactory", null);

        var (vm, _) = Build(store, withFdtd: false);
        var pdkInfo = vm.AvailableCustomPdks.ShouldHaveSingleItem();
        pdkInfo.Name.ShouldBe("My SiN Lib");

        vm.SelectedCustomPdk.ShouldBe(pdkInfo); // ctor pre-selects the only existing PDK
        vm.SelectedProcess.ShouldBe(pdkInfo.Process); // process is inherited from the PDK

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        var pdk = new PdkLoader().LoadFromFileForEditing(pdkInfo.FilePath);
        pdk.Components.Count.ShouldBe(2); // appended, not replacing the file
        pdk.Components.ShouldContain(c => c.Name == "My Comp");
    }

    [Fact]
    public async Task Save_setsSavedFilePath_toTheActualWrittenFile_notTheProcessDefaultPath()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "P" };
        var path = store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var (vm, _) = Build(store, new List<ProcessDefinition> { process }, withFdtd: false);

        vm.SavedFilePath.ShouldBeNull(); // nothing saved yet

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        // Must be the named-PDK file the store actually wrote to — never ResolvePath(process)'s
        // per-process default file, which is wrong for a PDK-first save (the bug
        // NewComponentWindowLauncher.OnSaved used to have).
        vm.SavedFilePath.ShouldBe(path);
        vm.SavedFilePath.ShouldNotBe(store.ResolvePath(process));
    }

    [Fact]
    public async Task FdtdFailure_savesBlackBox_andReportsTheError()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var (vm, fdtd) = Build(store, new List<ProcessDefinition> { process }, withFdtd: true);

        fdtd.Setup(f => f.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Available(""));
        fdtd.Setup(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdSMatrixResult.Fail("solver blew up"));

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("solver blew up"); // failure reported, never fabricated
        vm.SavedDraft!.SMatrix.ShouldBeNull();
    }

    [Fact]
    public void AvailableBackends_alwaysOffersBothBackends()
    {
        var store = Store();
        var (vm, _) = Build(store, withFdtd: false);

        vm.AvailableBackends.Count.ShouldBe(2); // own-code mode is the only mode now
        vm.AvailableBackends.ShouldContain(GeometryBackend.GdsFactory);
        vm.AvailableBackends.ShouldContain(GeometryBackend.Nazca);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
