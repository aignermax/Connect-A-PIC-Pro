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
/// Covers the PDK-first redesign of <see cref="NewComponentViewModel"/>: choosing between an
/// existing named custom PDK (<see cref="NewComponentViewModel.SelectedCustomPdk"/>, process
/// inherited and read-only) and creating a new one (<see cref="NewComponentViewModel.IsNewPdk"/>,
/// name + process chosen), and routing <c>Save</c> to <see cref="UserPdkStore.SaveToNamedPdk"/>
/// or <see cref="UserPdkStore.AppendToExistingPdk"/> accordingly. Function-reference mode no
/// longer exists — every save is the user's own code.
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
    public async Task NoCustomPdks_defaultsToNewPdk_andSavesToNamedPdk()
    {
        var store = Store();
        var (vm, _) = Build(store, withFdtd: false);

        vm.AvailableCustomPdks.ShouldBeEmpty();
        vm.IsNewPdk.ShouldBeTrue(); // Test A: no custom PDKs yet -> defaults to "new PDK"

        vm.SelectedProcess = vm.Processes[0];
        vm.NewPdkName = "My New PDK";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.RawCode.ShouldContain("gf.components.coupler");

        store.ListCustomPdks().ShouldContain(i => i.Name == "My New PDK" && i.Process.Name == "P");
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

        vm.SelectedCustomPdk = pdkInfo;

        vm.IsNewPdk.ShouldBeFalse(); // Test B: selecting an existing PDK switches out of "new"
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
        var (vm, _) = Build(store, withFdtd: false);
        vm.SelectedProcess = vm.Processes[0];
        vm.NewPdkName = "My New PDK";

        vm.SavedFilePath.ShouldBeNull(); // nothing saved yet

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        // Must be the named-PDK file the store actually wrote to (SaveToNamedPdk's return
        // value) — never ResolvePath(process)'s per-process default file, which is wrong for
        // a PDK-first save (the bug NewComponentWindowLauncher.OnSaved used to have).
        vm.SavedFilePath.ShouldBe(store.ResolveNamedPath("My New PDK"));
        vm.SavedFilePath.ShouldNotBe(store.ResolvePath(vm.Processes[0]));
    }

    [Fact]
    public async Task CanSave_isFalse_whenNewPdkNameIsBlank()
    {
        var store = Store();
        var (vm, _) = Build(store, withFdtd: false);
        vm.SelectedProcess = vm.Processes[0];
        vm.NewPdkName = "";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.HasPreview.ShouldBeTrue();

        vm.IsNewPdk.ShouldBeTrue();
        vm.SaveCommand.CanExecute(null).ShouldBeFalse(); // Test C: new PDK requires a name

        vm.NewPdkName = "Now Named";
        vm.SaveCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task FdtdFailure_savesBlackBox_andReportsTheError()
    {
        var store = Store();
        var (vm, fdtd) = Build(store, withFdtd: true);
        vm.SelectedProcess = vm.Processes[0];
        vm.NewPdkName = "PDK";

        fdtd.Setup(f => f.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Available(""));
        fdtd.Setup(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdSMatrixResult.Fail("solver blew up"));

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("solver blew up"); // Test D: failure reported, never fabricated
        vm.SavedDraft!.SMatrix.ShouldBeNull();
    }

    [Fact]
    public void AvailableBackends_alwaysOffersBothBackends()
    {
        var store = Store();
        var (vm, _) = Build(store, withFdtd: false);

        vm.AvailableBackends.Count.ShouldBe(2); // Test E: own-code mode is the only mode now
        vm.AvailableBackends.ShouldContain(GeometryBackend.GdsFactory);
        vm.AvailableBackends.ShouldContain(GeometryBackend.Nazca);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
