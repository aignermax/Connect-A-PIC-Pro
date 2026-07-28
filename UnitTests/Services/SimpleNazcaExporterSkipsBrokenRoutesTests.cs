using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// A blocked/invalid/routeless connection must never render as geometry in the Nazca
/// export — the design still exports, but that connection's geometry is left out
/// (field report: unroutable tight layouts otherwise leak a red/dashed path straight
/// into the GDS). Netlist export is intentionally unaffected — a blocked connection is
/// still part of the circuit topology.
/// </summary>
public class SimpleNazcaExporterSkipsBrokenRoutesTests
{
    [Fact]
    public void Export_OneBlockedOneValidConnection_OnlyValidConnectionRenders()
    {
        var canvas = new DesignCanvasViewModel();
        var (valid, blocked) = AddTwoConnections(canvas);
        valid.RestoreCachedPath(StraightPath());
        var blockedPath = StraightPath();
        blockedPath.IsBlockedFallback = true;
        blocked.RestoreCachedPath(blockedPath);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=200.00");     // valid A→B connection
        script.ShouldNotContain("nd.strt(length=300.00");  // blocked C→D connection
    }

    [Fact]
    public void Export_InvalidGeometryConnection_IsOmitted()
    {
        var canvas = new DesignCanvasViewModel();
        var (valid, invalid) = AddTwoConnections(canvas);
        valid.RestoreCachedPath(StraightPath());
        var invalidPath = StraightPath();
        invalidPath.IsInvalidGeometry = true;
        invalid.RestoreCachedPath(invalidPath);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=200.00");
        script.ShouldNotContain("nd.strt(length=300.00");
    }

    [Fact]
    public void Export_RouteAllConnectionsValid_NoConnectionIsOmitted()
    {
        var canvas = new DesignCanvasViewModel();
        var (first, second) = AddTwoConnections(canvas);
        first.RestoreCachedPath(StraightPath());
        second.RestoreCachedPath(StraightPath());

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("nd.strt(length=200.00");
        script.ShouldContain("nd.strt(length=300.00");
    }

    [Fact]
    public void Export_AllConnectionsBlocked_StillExportsComponentsButNoConnectionGeometry()
    {
        var canvas = new DesignCanvasViewModel();
        var (first, second) = AddTwoConnections(canvas);
        var blockedPath = StraightPath();
        blockedPath.IsBlockedFallback = true;
        first.RestoreCachedPath(blockedPath);
        var invalidPath = StraightPath();
        invalidPath.IsInvalidGeometry = true;
        second.RestoreCachedPath(invalidPath);

        var script = new SimpleNazcaExporter().Export(canvas);

        // The export does not abort: every component is still placed.
        script.ShouldContain("comp_0 =");
        script.ShouldContain("comp_1 =");
        script.ShouldContain("comp_2 =");
        script.ShouldContain("comp_3 =");
        script.ShouldNotContain("nd.strt(length=200.00");
        script.ShouldNotContain("nd.strt(length=300.00");
    }

    /// <summary>
    /// Places four components (A, B at x=0/200; C, D at x=1000/1300) and connects A→B and
    /// C→D with straight, forward-facing pins — distinct lengths (200 vs 300 µm) let each
    /// connection's exported line be identified unambiguously.
    /// </summary>
    private static (WaveguideConnection FirstAtoB, WaveguideConnection SecondCtoD) AddTwoConnections(
        DesignCanvasViewModel canvas)
    {
        var a = ComponentAt("A", 0);
        var b = ComponentAt("B", 200);
        var c = ComponentAt("C", 1000);
        var d = ComponentAt("D", 1300);
        canvas.Components.Add(new ComponentViewModel(a));
        canvas.Components.Add(new ComponentViewModel(b));
        canvas.Components.Add(new ComponentViewModel(c));
        canvas.Components.Add(new ComponentViewModel(d));

        var firstConnection = new WaveguideConnection
        {
            StartPin = a.PhysicalPins.Single(),
            EndPin = b.PhysicalPins.Single(),
        };
        var secondConnection = new WaveguideConnection
        {
            StartPin = c.PhysicalPins.Single(),
            EndPin = d.PhysicalPins.Single(),
        };
        canvas.Connections.Add(new WaveguideConnectionViewModel(firstConnection));
        canvas.Connections.Add(new WaveguideConnectionViewModel(secondConnection));
        return (firstConnection, secondConnection);
    }

    private static Component ComponentAt(string identifier, double x)
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
        });
        return comp;
    }

    private static RoutedPath StraightPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 1, 0, 0));
        return path;
    }
}
