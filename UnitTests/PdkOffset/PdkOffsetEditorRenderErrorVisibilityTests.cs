using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using System.Threading;

namespace UnitTests.PdkOffset;

/// <summary>
/// Field bug (round 5): the adiabatic couplers rendered a "successful" GDS
/// preview with zero polygons — the editor showed only the dashed Lunima box
/// and NO error anywhere. These tests pin the new visibility contract of
/// <see cref="PdkOffsetEditorViewModel.OverlayErrorText"/>: any failed render
/// AND any successful-but-empty render must surface a message at the
/// component; a render with real geometry must clear it.
/// </summary>
public class PdkOffsetEditorRenderErrorVisibilityTests
{
    public PdkOffsetEditorRenderErrorVisibilityTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    private static PdkComponentDraft BuildDraft(string name = "Adiabatic Coupler TE 1550") => new()
    {
        Name = name,
        NazcaFunction = "ebeam_adiabatic_te1550",
        WidthMicrometers = 195.9,
        HeightMicrometers = 6.3,
        NazcaOriginOffsetX = -0.05,
        NazcaOriginOffsetY = 3.0,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "port 1", OffsetXMicrometers = 0.05, OffsetYMicrometers = 1.5 },
        },
    };

    private static (PdkOffsetEditorViewModel vm, Mock<NazcaComponentPreviewService> renderer) BuildVm()
    {
        var renderer = new Mock<NazcaComponentPreviewService>(
            "py", "nazca.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        var vm = new PdkOffsetEditorViewModel(
            new PdkLoader(), new PdkJsonSaver(), new PdkManagerViewModel(), renderer.Object)
        {
            // Synchronous marshaller — no Avalonia dispatcher in unit tests.
            UiThreadMarshaller = action => { action(); return Task.CompletedTask; },
        };
        return (vm, renderer);
    }

    private static NazcaPreviewResult SuccessResult(int polygonCount, string? polygonWarning = null)
    {
        var polygons = Enumerable.Range(0, polygonCount)
            .Select(_ => new NazcaPreviewPolygon
            {
                Layer = 1,
                Vertices = new List<(double, double)> { (0, 0), (1, 0), (1, 1) },
            })
            .ToList();
        return new NazcaPreviewResult
        {
            Success = true,
            XMin = 0, YMin = -3.3, XMax = 195.95, YMax = 3.0,
            Polygons = polygons,
            PolygonWarning = polygonWarning,
            Pins = new List<NazcaPreviewPin> { new() { Name = "opt1", X = 0.1, Y = 1.5 } },
        };
    }

    private static async Task SelectAndRender(
        PdkOffsetEditorViewModel vm, PdkComponentDraft draft)
    {
        vm.Components.Add(new PdkComponentOffsetItemViewModel(draft, "SiEPIC EBeam PDK"));
        vm.SelectedComponent = vm.Components[0];
        // Selection fires the render fire-and-forget; await an explicit second
        // pass so the assertion point is deterministic.
        await vm.TriggerNazcaRenderAsync(draft);
    }

    [Fact]
    public async Task FailedRender_SurfacesErrorAtComponent()
    {
        var (vm, renderer) = BuildVm();
        renderer.Setup(r => r.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NazcaPreviewResult.Fail("boom: module not found"));

        await SelectAndRender(vm, BuildDraft());

        vm.OverlayErrorText.ShouldContain("Adiabatic Coupler TE 1550");
        vm.OverlayErrorText.ShouldContain("boom: module not found");
        vm.HasNazcaOverlay.ShouldBeFalse();
    }

    [Fact]
    public async Task SuccessfulRenderWithZeroPolygons_SurfacesWarningAtComponent()
    {
        // The exact field scenario: success=true, pins present, polygons empty.
        var (vm, renderer) = BuildVm();
        renderer.Setup(r => r.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(polygonCount: 0, polygonWarning: "No polygons found in cell"));

        await SelectAndRender(vm, BuildDraft());

        vm.OverlayErrorText.ShouldContain("Adiabatic Coupler TE 1550");
        vm.OverlayErrorText.ShouldContain("No polygons found in cell");
        // Pins are still valid — the overlay (pin stubs, calibration) must stay usable.
        vm.HasNazcaOverlay.ShouldBeTrue();
    }

    [Fact]
    public async Task SuccessfulRenderWithGeometry_ClearsErrorText()
    {
        var (vm, renderer) = BuildVm();
        renderer.SetupSequence(r => r.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NazcaPreviewResult.Fail("first attempt fails"))
            .ReturnsAsync(SuccessResult(polygonCount: 3));

        await SelectAndRender(vm, BuildDraft());   // consumes fail + success

        vm.OverlayErrorText.ShouldBeEmpty();
        vm.HasNazcaOverlay.ShouldBeTrue();
        vm.NazcaPolygons.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ThrowingRenderer_SurfacesErrorAtComponent()
    {
        var (vm, renderer) = BuildVm();
        renderer.Setup(r => r.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("renderer exploded"));

        await SelectAndRender(vm, BuildDraft());

        vm.OverlayErrorText.ShouldContain("renderer exploded");
        vm.HasNazcaOverlay.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingAnotherComponent_ResetsErrorText()
    {
        var (vm, renderer) = BuildVm();
        renderer.Setup(r => r.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NazcaPreviewResult.Fail("boom"));

        var first = BuildDraft("First");
        await SelectAndRender(vm, first);
        vm.OverlayErrorText.ShouldNotBeEmpty();

        // Selecting a different component must clear the stale banner even
        // before the new render completes.
        var second = BuildDraft("Second");
        vm.Components.Add(new PdkComponentOffsetItemViewModel(second, "SiEPIC EBeam PDK"));

        // Renderer for the second component never completes (pending task) —
        // the banner must already be reset by the selection change itself.
        var pending = new TaskCompletionSource<NazcaPreviewResult>();
        renderer.Setup(r => r.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        vm.SelectedComponent = vm.Components[1];

        vm.OverlayErrorText.ShouldBeEmpty();
    }
}
