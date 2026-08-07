using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Export layer fidelity of frozen group paths (GDS layer round-trip): a frozen path
/// tagged with the (layer, datatype) of the polygon it was imported from must export
/// its segments on THAT layer — optical geometry on the tag itself, a metal trace with
/// the tag winning over the process metal default — while untagged paths keep the
/// historical default emission exactly. A PIN-LESS tagged path is an imported route
/// polygon's outline ring (no centerline exists for it) and exports as one verbatim
/// polygon on the tagged layer — per-edge waveguides would double every route on
/// re-import.
/// </summary>
public class SimpleNazcaExporterFrozenPathLayerTests
{
    [Fact]
    public void Export_FrozenPathWithLayerTag_EmitsSegmentsOnSourceLayer()
    {
        var canvas = CanvasWith(CreateGroup(path: StraightPath(), layer: 31, dataType: 5));

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=10.00, layer=(31, 5)).put(0.00, 0.00, 0.00)");
    }

    [Fact]
    public void Export_FrozenPathWithoutLayerTag_KeepsDefaultEmission()
    {
        var canvas = CanvasWith(CreateGroup(path: StraightPath(), layer: null, dataType: null));

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=10.00).put(0.00, 0.00, 0.00)");
        // An untagged path must not invent a layer override.
        script.ShouldNotContain("layer=(31");
    }

    [Fact]
    public void Export_MetalFrozenPathWithLayerTag_TagWinsOverProcessMetalDefault()
    {
        // A frozen METAL trace (both pins electrical) whose source polygon sat on a
        // custom metal layer: the polygon's layer wins over the process default
        // (11, 0); the metal WIDTH is the process style's — the tag carries no width.
        var child = CreateChild("pad_cell");
        var startPin = ElectricalPin("e1", child, offsetX: 0);
        var endPin = ElectricalPin("e2", child, offsetX: 10);
        var group = CreateGroup(path: StraightPath(), layer: 45, dataType: 2,
            child: child, startPin: startPin, endPin: endPin);
        var canvas = CanvasWith(group);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("width=10.00, layer=(45, 2)");
        script.ShouldNotContain("layer=(11, 0)");
    }

    [Fact]
    public void Export_MetalFrozenPathWithoutLayerTag_KeepsProcessMetalDefault()
    {
        var child = CreateChild("pad_cell");
        var startPin = ElectricalPin("e1", child, offsetX: 0);
        var endPin = ElectricalPin("e2", child, offsetX: 10);
        var group = CreateGroup(path: StraightPath(), layer: null, dataType: null,
            child: child, startPin: startPin, endPin: endPin);
        var canvas = CanvasWith(group);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("width=10.00, layer=(11, 0)");
    }

    [Fact]
    public void Export_PinLessTaggedRingPath_EmitsVerbatimPolygonOnSourceLayer()
    {
        // A pin-less tagged frozen path holds an imported route polygon's OUTLINE ring —
        // the honest round-trip is the polygon itself, not one waveguide per ring edge
        // (which doubles every route on re-import, see GdsReexportIdempotencyTests).
        var canvas = CanvasWith(CreateGroup(path: RingPath(), layer: 31, dataType: 5));

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain(
            "nd.Polygon(points=[(0.00,0.00),(10.00,0.00),(10.00,-1.00),(0.00,-1.00)], layer=(31, 5)).put(0, 0)");
        script.ShouldNotContain("nd.strt(length=10.00, layer=(31, 5))");
    }

    [Fact]
    public void Export_PinLessUntaggedRingPath_KeepsSegmentEmission()
    {
        // Without the import's source-layer tag the ring shape stays historical:
        // per-segment waveguides on the default layer (a hand-built pin-less path
        // has no claim to being a polygon outline).
        var canvas = CanvasWith(CreateGroup(path: RingPath(), layer: null, dataType: null));

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=10.00).put(0.00, 0.00, 0.00)");
        script.ShouldNotContain("nd.Polygon(");
    }

    [Fact]
    public void Export_PinnedRingPath_KeepsSegmentEmission()
    {
        // A frozen CONNECTION (both pins set) is centerline geometry even when its
        // cached route happens to chain into a ring — it keeps the segment emission.
        var child = CreateChild("splitter_1x2");
        var startPin = OpticalPin("in", child, offsetX: 0);
        var endPin = OpticalPin("out", child, offsetX: 10);
        var group = CreateGroup(path: RingPath(), layer: 31, dataType: 5,
            child: child, startPin: startPin, endPin: endPin);
        var canvas = CanvasWith(group);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=10.00, layer=(31, 5))");
        script.ShouldNotContain("nd.Polygon(points=[(0.00,0.00),(10.00,0.00)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RoutedPath StraightPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));
        return path;
    }

    /// <summary>A closed 10×1 µm rectangle outline — the shape
    /// <c>GdsFrozenRoutePathFactory.Create</c> traces from an imported route polygon.</summary>
    private static RoutedPath RingPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));
        path.Segments.Add(new StraightSegment(10, 0, 10, 1, 90));
        path.Segments.Add(new StraightSegment(10, 1, 0, 1, 180));
        path.Segments.Add(new StraightSegment(0, 1, 0, 0, -90));
        return path;
    }

    private static ComponentGroup CreateGroup(
        RoutedPath path, int? layer, int? dataType,
        Component? child = null, PhysicalPin? startPin = null, PhysicalPin? endPin = null)
    {
        var group = new ComponentGroup("LayerGroup");
        group.AddChild(child ?? CreateChild("splitter_1x2"));
        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = path,
            StartPin = startPin,
            EndPin = endPin,
            Layer = layer,
            DataType = dataType,
        });
        return group;
    }

    private static DesignCanvasViewModel CanvasWith(ComponentGroup group)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(group));
        return canvas;
    }

    private static PhysicalPin ElectricalPin(string name, Component parent, double offsetX) =>
        new()
        {
            Name = name,
            ParentComponent = parent,
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = 0,
            AngleDegrees = 0,
            LogicalPin = new Pin(name, 0, MatterType.Electricity, RectSide.Up),
        };

    private static PhysicalPin OpticalPin(string name, Component parent, double offsetX) =>
        new()
        {
            Name = name,
            ParentComponent = parent,
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = 0,
            AngleDegrees = 0,
            LogicalPin = new Pin(name, 0, MatterType.Light, RectSide.Up),
        };

    private static Component CreateChild(string nazcaFunctionName)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: nazcaFunctionName,
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: nazcaFunctionName,
            rotationCounterClock: DiscreteRotation.R0
        );

        component.WidthMicrometers = 50;
        component.HeightMicrometers = 50;
        return component;
    }
}
