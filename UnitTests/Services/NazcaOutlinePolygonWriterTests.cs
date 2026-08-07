using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Whole-Layout export of group outline polygons (the render-only background geometry
/// of a GDS import): every polygon must land in the script as an nd.Polygon on its
/// ORIGINAL (layer, datatype), at the same world positions the canvas renders —
/// previously this geometry vanished on export entirely.
/// </summary>
public class NazcaOutlinePolygonWriterTests
{
    [Fact]
    public void Export_GroupWithOutlinePolygons_EmitsPolygonsOnTheirOriginalLayers()
    {
        // Group at (100, 50), unrotated: the local outline points shift 1:1 into
        // world space, then Y-negate into Nazca space like every script coordinate.
        var group = CreateGroup(x: 100, y: 50);
        group.OutlinePolygons = new[]
        {
            Rect(layer: 111, dataType: 0, x0: 0, y0: 0, x1: 10, y1: 5),
            Rect(layer: 31, dataType: 5, x0: 20, y0: 0, x1: 30, y1: 5),
        };
        var canvas = CanvasWith(group);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("# Group 'LayerGroup' outline geometry (original GDS layers)");
        // The GDS closing repeat is dropped — nd.Polygon closes the ring itself.
        script.ShouldContain(
            "nd.Polygon(points=[(100.00,-50.00),(110.00,-50.00),(110.00,-55.00),(100.00,-55.00)], layer=(111, 0)).put(0, 0)");
        script.ShouldContain(
            "nd.Polygon(points=[(120.00,-50.00),(130.00,-50.00),(130.00,-55.00),(120.00,-55.00)], layer=(31, 5)).put(0, 0)");
    }

    [Fact]
    public void Export_NestedGroupOutlinePolygons_AreEmittedRecursively()
    {
        var inner = CreateGroup(x: 0, y: 0);
        inner.GroupName = "Inner";
        inner.OutlinePolygons = new[] { Rect(layer: 111, dataType: 0, x0: 0, y0: 0, x1: 4, y1: 2) };
        var outer = CreateGroup(x: 0, y: 0);
        outer.AddChild(inner);
        var canvas = CanvasWith(outer);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("# Group 'Inner' outline geometry (original GDS layers)");
        script.ShouldContain("layer=(111, 0)");
    }

    [Fact]
    public void Export_GroupWithoutOutlinePolygons_EmitsNoOutlineSection()
    {
        var canvas = CanvasWith(CreateGroup(x: 0, y: 0));

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldNotContain("outline geometry");
    }

    [Fact]
    public void ExportPartial_ExcludedGroup_EmitsNoOutlinePolygons()
    {
        var group = CreateGroup(x: 0, y: 0);
        group.OutlinePolygons = new[] { Rect(layer: 111, dataType: 0, x0: 0, y0: 0, x1: 4, y1: 2) };
        var canvas = CanvasWith(group);

        var script = new SimpleNazcaExporter().ExportPartial(
            canvas, include: _ => false, topCellName: "Partial");

        script.ShouldNotContain("outline geometry");
        script.ShouldNotContain("layer=(111, 0)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Closed axis-aligned rectangle ring (5 points, first repeated), GDS convention.</summary>
    private static OutlinePolygon Rect(int layer, int dataType, double x0, double y0, double x1, double y1) =>
        new()
        {
            Layer = layer,
            DataType = dataType,
            Points = new[]
            {
                new OutlinePoint(x0, y0), new OutlinePoint(x1, y0),
                new OutlinePoint(x1, y1), new OutlinePoint(x0, y1),
                new OutlinePoint(x0, y0),
            },
        };

    private static ComponentGroup CreateGroup(double x, double y)
    {
        var group = new ComponentGroup("LayerGroup")
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 40,
            HeightMicrometers = 10,
        };
        group.AddChild(CreateChild("splitter_1x2"));
        return group;
    }

    private static DesignCanvasViewModel CanvasWith(ComponentGroup group)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(group));
        return canvas;
    }

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
