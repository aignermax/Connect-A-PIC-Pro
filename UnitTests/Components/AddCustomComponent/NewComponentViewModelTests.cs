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
/// Covers <see cref="NewComponentViewModel"/>: geometry preview + optional FDTD S-matrix
/// recompute + save into a process's user PDK, with a hard rule that a missing or failed
/// FDTD run always saves as a black box, never a fabricated S-matrix.
/// </summary>
public class NewComponentViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-vm-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 2,
        Polygons = new List<NazcaPreviewPolygon> { new() { Layer = 1, Vertices = new List<(double, double)> { (0, 0), (10, 0), (10, 2), (0, 2) } } },
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 1, Angle = 180 }, new() { Name = "o2", X = 10, Y = 1, Angle = 0 } }
    };

    private (NewComponentViewModel vm, Mock<IFdtdSMatrixService> fdtd) Build(bool withFdtd = true)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var vm = new NewComponentViewModel(extractor, withFdtd ? fdtd.Object : null, store,
            new List<ProcessDefinition> { new() { Name = "P" } });
        vm.ComponentName = "My Comp";
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        vm.SelectedProcess = vm.Processes[0];
        vm.NewPdkName = "My PDK"; // no custom PDKs exist yet -> IsNewPdk defaults true, needs a name
        return (vm, fdtd);
    }

    [Fact]
    public async Task Save_without_fdtd_writes_a_black_box_component()
    {
        var (vm, _) = Build(withFdtd: false);
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.SMatrix.ShouldBeNull();      // black box, no invented physics
        vm.SavedDraft.Pins.Count.ShouldBe(2);
        vm.SavedDraft.RawCode.ShouldContain("gf.components.coupler");
        vm.SavedDraft.GdsFactoryFunction.ShouldBeNull(); // always own-code now, never a reference
    }

    [Fact]
    public async Task ComputeSMatrix_failure_does_not_produce_a_model()
    {
        var (vm, fdtd) = Build();
        fdtd.Setup(f => f.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Available(""));
        fdtd.Setup(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdSMatrixResult.Fail("solver blew up"));

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("solver blew up");
        vm.SavedDraft!.SMatrix.ShouldBeNull();       // failed FDTD => still no model, never fake
    }

    [Fact]
    public async Task Save_without_fdtd_reports_a_confirmation()
    {
        var (vm, _) = Build(withFdtd: false);
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.StatusText.ShouldContain("Saved");   // black-box save still confirms
    }

    [Fact]
    public async Task Save_requires_a_name()
    {
        var (vm, _) = Build(withFdtd: false);
        vm.ComponentName = "   ";
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);
        vm.SavedDraft.ShouldBeNull();
    }

    [Fact]
    public async Task Changing_the_geometry_after_preview_invalidates_it_and_blocks_save()
    {
        var (vm, _) = Build(withFdtd: false);
        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.HasPreview.ShouldBeTrue();
        vm.SaveCommand.CanExecute(null).ShouldBeTrue();

        // Edit the code without re-previewing: the rendered geometry no longer matches what
        // would be saved, so Save must become impossible (drift guard, #656 review).
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.mmi1x2()";

        vm.HasPreview.ShouldBeFalse();
        vm.SaveCommand.CanExecute(null).ShouldBeFalse();

        // Even if the command body is invoked directly (bypassing CanExecute), the cleared
        // preview makes Save bail out — no mixed-geometry draft is ever persisted.
        await vm.SaveCommand.ExecuteAsync(null);
        vm.SavedDraft.ShouldBeNull();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
