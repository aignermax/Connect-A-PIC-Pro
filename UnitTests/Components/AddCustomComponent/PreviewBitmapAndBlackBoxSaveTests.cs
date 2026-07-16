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

public class PreviewBitmapAndBlackBoxSaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-vm-bitmap-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult OkWithPolygon() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 2,
        Polygons = new List<NazcaPreviewPolygon>
        {
            new() { Layer = 1, Vertices = new List<(double, double)> { (0, 0), (10, 0), (10, 2), (0, 2) } }
        },
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 1, Angle = 180 }, new() { Name = "o2", X = 10, Y = 1, Angle = 0 } }
    };

    private static PdkComponentDraft SeedComponent(string n) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private (NewComponentViewModel vm, Mock<IFdtdSMatrixService> fdtd) Build(bool withFdtd = false)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(OkWithPolygon());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var vm = new NewComponentViewModel(extractor, withFdtd ? fdtd.Object : null, store,
            new List<ProcessDefinition> { process });
        vm.ComponentName = "My Comp";
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        return (vm, fdtd);
    }

    // PreviewBitmap stays null in the headless test host (no render backend); assert tolerantly.
    [Fact]
    public async Task RunPreview_withPolygons_setsHasPreview_andDoesNotCrashRasterizing()
    {
        var (vm, _) = Build();
        vm.PreviewBitmap.ShouldBeNull();

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.HasPreview.ShouldBeTrue();
        if (vm.PreviewBitmap is null)
        {
            return;
        }
        vm.PreviewBitmap.PixelSize.Width.ShouldBeGreaterThan(0);
        vm.PreviewBitmap.PixelSize.Height.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task RunPreview_failure_clearsAnyPreviousBitmap()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.SetupSequence(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkWithPolygon())
            .ReturnsAsync(NazcaPreviewResult.Fail("boom"));
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var vm = new NewComponentViewModel(extractor, null, store, new List<ProcessDefinition> { process });
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.HasPreview.ShouldBeFalse();
        vm.PreviewBitmap.ShouldBeNull();
    }

    [Fact]
    public async Task Save_withoutCompute_savesBlackBox_andStatusNamesIt()
    {
        var (vm, _) = Build(withFdtd: false);
        await vm.RunPreviewCommand.ExecuteAsync(null);

        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.SMatrix.ShouldBeNull();
        vm.StatusText.ShouldContain("black box");
    }

    [Fact]
    public async Task CanSave_isTrue_assoonAsAPdkIsSelected_evenWithoutAPriorPreviewOrCompute()
    {
        var (vm, _) = Build(withFdtd: true);
        vm.SelectedCustomPdk.ShouldNotBeNull();
        vm.SaveCommand.CanExecute(null).ShouldBeTrue();

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.SaveCommand.CanExecute(null).ShouldBeTrue();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
