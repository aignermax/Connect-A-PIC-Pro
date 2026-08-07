using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Export layer fidelity of route-derived connections (<see cref="WaveguideConnection.SourceGdsLayer"/>):
/// a connection tagged with its import source's unambiguous (layer, datatype) exports its
/// geometry on THAT layer — optical segments and metal traces alike (for metal the tag wins
/// over the process default layer, the metal width stays) — and untagged connections keep
/// the historical default emission.
/// </summary>
public class SimpleNazcaExporterConnectionSourceLayerTests
{
    [Fact]
    public void Export_TaggedOpticalConnection_EmitsSegmentsOnSourceLayer()
    {
        var canvas = new DesignCanvasViewModel();
        var connection = AddConnection(canvas, sourceLayer: 3, sourceDataType: 0);

        var script = new SimpleNazcaExporter().Export(canvas);

        // Single-straight fast path: geometry computed from the pins, layer from the tag.
        script.ShouldContain("nd.strt(length=200.00, layer=(3, 0)).put(0.00, 0.00, 0.00)");
    }

    [Fact]
    public void Export_UntaggedOpticalConnection_KeepsDefaultEmission()
    {
        var canvas = new DesignCanvasViewModel();
        AddConnection(canvas, sourceLayer: null, sourceDataType: null);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=200.00).put(0.00, 0.00, 0.00)");
        script.ShouldNotContain("layer=(3, 0)");
    }

    [Fact]
    public void Export_TaggedMetalConnection_TagWinsOverProcessMetalDefault()
    {
        var canvas = new DesignCanvasViewModel();
        AddConnection(canvas, sourceLayer: 45, sourceDataType: 2, electrical: true);

        var script = new SimpleNazcaExporter().Export(canvas);

        // The metal trace keeps the process WIDTH but takes the source polygon's layer.
        script.ShouldContain("width=10.00, layer=(45, 2)");
        script.ShouldNotContain("layer=(11, 0)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Places A at x=0 and B at x=200, connects A.p0 → B.p0 with a cached straight,
    /// and tags the connection with the given source layer (or none).
    /// </summary>
    private static WaveguideConnection AddConnection(
        DesignCanvasViewModel canvas, int? sourceLayer, int? sourceDataType, bool electrical = false)
    {
        var a = ComponentAt("A", 0, electrical);
        var b = ComponentAt("B", 200, electrical);
        canvas.Components.Add(new ComponentViewModel(a));
        canvas.Components.Add(new ComponentViewModel(b));

        var connection = new WaveguideConnection
        {
            StartPin = a.PhysicalPins.Single(),
            EndPin = b.PhysicalPins.Single(),
            SourceGdsLayer = sourceLayer,
            SourceGdsDataType = sourceDataType,
        };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 200, 0, 0));
        connection.RestoreCachedPath(path);
        canvas.Connections.Add(new WaveguideConnectionViewModel(connection));
        return connection;
    }

    private static Component ComponentAt(string identifier, double x, bool electrical)
    {
        var comp = TestComponentFactory.CreateBasicComponent();
        comp.Identifier = identifier;
        comp.PhysicalX = x;
        comp.PhysicalY = 0;
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "p0",
            ParentComponent = comp,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = 0,
            LogicalPin = electrical
                ? new Pin("p0", 0, MatterType.Electricity, RectSide.Up)
                : null,
        });
        return comp;
    }
}
