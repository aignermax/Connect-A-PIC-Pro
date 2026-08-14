using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Canvas-level pin-less frozen paths (imported route geometry released by ungrouping,
/// issue #856) must export exactly like they did while still inside the group: a tagged
/// outline ring emits one verbatim polygon on its source layer in the Nazca exporter,
/// and segments emit unchanged in the gdsfactory exporter.
/// </summary>
public class CanvasFrozenPathExportTests
{
    [Fact]
    public void NazcaExport_CanvasPinLessTaggedRingPath_EmitsVerbatimPolygonOnSourceLayer()
    {
        var canvas = CanvasWithPath(RingPath(), layer: 31, dataType: 5);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain(
            "nd.Polygon(points=[(0.00,0.00),(10.00,0.00),(10.00,-1.00),(0.00,-1.00)], layer=(31, 5)).put(0, 0)");
    }

    [Fact]
    public void NazcaExport_CanvasPathMatchesGroupInternalPathOutput()
    {
        // The exact geometry must export identically whether it lives in the group
        // or was released to the canvas store by ungrouping.
        var groupCanvas = CanvasWithChild();
        var group = new ComponentGroup("ImportGroup");
        group.AddChild(CreateChild("splitter_1x2"));
        group.AddInternalPath(PinLessPath(RingPath(), 31, 5));
        groupCanvas.Components.Clear();
        groupCanvas.Components.Add(new ComponentViewModel(group));

        var canvasCanvas = CanvasWithChild();
        canvasCanvas.CanvasFrozenPaths.Add(new CanvasFrozenPathViewModel(PinLessPath(RingPath(), 31, 5)));

        var groupScript = new SimpleNazcaExporter().Export(groupCanvas);
        var canvasScript = new SimpleNazcaExporter().Export(canvasCanvas);

        const string polygon =
            "nd.Polygon(points=[(0.00,0.00),(10.00,0.00),(10.00,-1.00),(0.00,-1.00)], layer=(31, 5)).put(0, 0)";
        groupScript.ShouldContain(polygon);
        canvasScript.ShouldContain(polygon);
    }

    [Fact]
    public void NazcaExport_CanvasPinLessUntaggedPath_KeepsSegmentEmission()
    {
        var canvas = CanvasWithPath(StraightPath(), layer: null, dataType: null);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=10.00).put(0.00, 0.00, 0.00)");
    }

    [Fact]
    public void GdsFactoryExport_CanvasPinLessPath_EmitsSegments()
    {
        var canvas = CanvasWithPath(StraightPath(), layer: null, dataType: null);

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs));

        script.ShouldContain("gf.components.straight(length=10.00");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DesignCanvasViewModel CanvasWithPath(RoutedPath path, int? layer, int? dataType)
    {
        var canvas = CanvasWithChild();
        canvas.CanvasFrozenPaths.Add(new CanvasFrozenPathViewModel(PinLessPath(path, layer, dataType)));
        return canvas;
    }

    private static DesignCanvasViewModel CanvasWithChild()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(CreateChild("splitter_1x2")));
        return canvas;
    }

    private static FrozenWaveguidePath PinLessPath(RoutedPath path, int? layer, int? dataType) => new()
    {
        Path = path,
        StartPin = null,
        EndPin = null,
        Layer = layer,
        DataType = dataType,
    };

    private static RoutedPath StraightPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));
        return path;
    }

    private static RoutedPath RingPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));
        path.Segments.Add(new StraightSegment(10, 0, 10, 1, 90));
        path.Segments.Add(new StraightSegment(10, 1, 0, 1, 180));
        path.Segments.Add(new StraightSegment(0, 1, 0, 0, -90));
        return path;
    }

    private static Component CreateChild(string nazcaFunctionName)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: nazcaFunctionName,
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: nazcaFunctionName,
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
        };
    }
}
