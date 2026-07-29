using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.MetalRouting;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MetalRouting;

/// <summary>
/// A crossing between two SEPARATE, independently valid routes is only a UI diagnostic
/// (<see cref="RoutedPath.IsBlockedFallback"/>) — <c>WaveguideConnectionManager</c>'s
/// sibling-crossing pass stamps it on EVERY unresolved crossing, including a metal trace
/// crossing a waveguide that a bridge marker legitimately resolves. The export filter must
/// never key off that flag (only <see cref="RoutedPath.IsPlaceholderGeometry"/> and
/// <see cref="RoutedPath.IsInvalidGeometry"/> do), so this reproduces the exact geometry
/// <c>NazcaMetalExportTests</c>/<c>GdsFactoryMetalExportTests</c> already prove crosses under
/// BridgeRequired, stamps the crossing connection <c>IsBlockedFallback</c> the way the manager's
/// pass would, and asserts BOTH sides still export with the bridge marker present — and that
/// nothing is reported as skipped OR as an unresolved crossing (the bridge marker resolves it).
/// </summary>
public class BridgeCrossingExportFilterTests
{
    [Fact]
    public void CrossingSiblings_BothExportWithBridgeMarker_NoSkipReported()
    {
        var (canvas, electrical, optical) = CreateCanvasWithCrossingConnections();

        // Sanity: the geometry actually crosses, and the manager's sibling-crossing pass
        // would stamp exactly this flag on it (simulated here) — never Placeholder/Invalid.
        PathIntersectionDetector.Crosses(electrical.RoutedPath!, optical.RoutedPath!).ShouldBeTrue(
            "the electrical and optical routes must genuinely cross for this test to be meaningful");
        optical.RoutedPath!.IsBlockedFallback = true;
        optical.RoutedPath!.IsPlaceholderGeometry.ShouldBeFalse();
        optical.RoutedPath!.IsInvalidGeometry.ShouldBeFalse();

        var spec = MetalRoutingSpec.Default with { CrossingPolicy = ElectricalCrossingPolicy.BridgeRequired };

        var nazcaSkipped = new List<string>();
        var nazcaUnresolved = new List<string>();
        var nazcaScript = new SimpleNazcaExporter().Export(
            canvas, metalSpec: spec, skippedConnections: nazcaSkipped, unresolvedCrossings: nazcaUnresolved);
        nazcaSkipped.ShouldBeEmpty();
        nazcaUnresolved.ShouldBeEmpty("a crossing the bridge marker resolves must not also be reported as unresolved");
        nazcaScript.ShouldContain("layer=(11, 0)");     // metal trace still rendered
        nazcaScript.ShouldContain("nd.strt(");          // optical waveguide still rendered
        nazcaScript.ShouldContain("# BRIDGE:");         // bridge marker at the crossing
        nazcaScript.ShouldContain("layer=BRIDGE_LAYER");

        var gdsSkipped = new List<string>();
        var gdsUnresolved = new List<string>();
        var gdsScript = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            metalSpec: spec, skippedConnections: gdsSkipped, unresolvedCrossings: gdsUnresolved);
        gdsSkipped.ShouldBeEmpty();
        gdsUnresolved.ShouldBeEmpty("a crossing the bridge marker resolves must not also be reported as unresolved");
        gdsScript.ShouldContain("layer=(11, 0)");
        gdsScript.ShouldContain("gf.components.straight(");
        gdsScript.ShouldContain("# BRIDGE:");
        gdsScript.ShouldContain("layer=BRIDGE_LAYER");
    }

    /// <summary>
    /// Same crossing geometry, but WITHOUT a metal connection to bridge over it (the crossing
    /// partner is another optical connection, so <see cref="ElectricalCrossingPolicy"/> never
    /// applies) — <c>WaveguideConnectionManager</c>'s sibling-crossing pass would still stamp
    /// <see cref="RoutedPath.IsBlockedFallback"/> on it, but there is no bridge to resolve it.
    /// The connection must still export (the geometry is real, not a placeholder) but must be
    /// reported through <c>unresolvedCrossings</c> so the layout gets a second look.
    /// </summary>
    [Fact]
    public void CrossingOpticalSiblings_NoMetalInvolved_ExportsButReportsUnresolvedCrossing()
    {
        var (canvas, other, optical) = CreateCanvasWithCrossingConnections(secondConnectionIsElectrical: false);

        PathIntersectionDetector.Crosses(other.RoutedPath!, optical.RoutedPath!).ShouldBeTrue(
            "the two optical routes must genuinely cross for this test to be meaningful");
        optical.RoutedPath!.IsBlockedFallback = true;
        optical.RoutedPath!.IsPlaceholderGeometry.ShouldBeFalse();
        optical.RoutedPath!.IsInvalidGeometry.ShouldBeFalse();

        var nazcaUnresolved = new List<string>();
        var nazcaScript = new SimpleNazcaExporter().Export(canvas, unresolvedCrossings: nazcaUnresolved);
        nazcaUnresolved.ShouldHaveSingleItem();
        nazcaScript.ShouldContain("nd.strt("); // geometry is still exported, not skipped

        var gdsUnresolved = new List<string>();
        var gdsScript = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            unresolvedCrossings: gdsUnresolved);
        gdsUnresolved.ShouldHaveSingleItem();
        gdsScript.ShouldContain("gf.components.straight(");
    }

    /// <summary>
    /// Same crossing layout as <c>NazcaMetalExportTests</c>/<c>GdsFactoryMetalExportTests</c>'
    /// <c>CreateCanvasWithCrossingConnections</c>: a horizontal trace at y≈45 and a diagonal
    /// optical route from (60,10) to (120,125) that crosses it. Each connection is routed
    /// standalone (own <see cref="WaveguideRouter"/>) purely to obtain real, valid, crossing
    /// geometry — this test's point is the EXPORT filter, not the router's own crossing-detection
    /// pass. <paramref name="secondConnectionIsElectrical"/> selects whether the horizontal trace
    /// is electrical (metal, bridgeable) or optical (no bridge policy applies).
    /// </summary>
    private static (DesignCanvasViewModel Canvas, WaveguideConnection Electrical, WaveguideConnection Optical)
        CreateCanvasWithCrossingConnections(bool secondConnectionIsElectrical = true)
    {
        var canvas = new DesignCanvasViewModel();

        var compA = CreateComponent("demo_pdk.pad_a", "A", x: 0, y: 40, width: 10, height: 10);
        var pinA = CreatePin(compA, "e1", 10, 5, 0, electrical: secondConnectionIsElectrical);
        compA.PhysicalPins.Add(pinA);
        var compB = CreateComponent("demo_pdk.pad_b", "B", x: 200, y: 40, width: 10, height: 10);
        var pinB = CreatePin(compB, "e1", 0, 5, 180, electrical: secondConnectionIsElectrical);
        compB.PhysicalPins.Add(pinB);
        canvas.Components.Add(new ComponentViewModel(compA));
        canvas.Components.Add(new ComponentViewModel(compB));
        var electrical = new WaveguideConnection { StartPin = pinA, EndPin = pinB };
        electrical.RecalculateTransmission(new WaveguideRouter());
        canvas.Connections.Add(new WaveguideConnectionViewModel(electrical));

        var compC = CreateComponent("demo_pdk.straight", "WG C", x: 40, y: 0, width: 20, height: 20);
        var pinC = CreatePin(compC, "b0", 20, 10, 0, electrical: false);
        compC.PhysicalPins.Add(pinC);
        var compD = CreateComponent("demo_pdk.straight", "WG D", x: 120, y: 115, width: 20, height: 20);
        var pinD = CreatePin(compD, "a0", 0, 10, 180, electrical: false);
        compD.PhysicalPins.Add(pinD);
        canvas.Components.Add(new ComponentViewModel(compC));
        canvas.Components.Add(new ComponentViewModel(compD));
        var optical = new WaveguideConnection { StartPin = pinC, EndPin = pinD };
        optical.RecalculateTransmission(new WaveguideRouter());
        optical.GetPathSegments().Count.ShouldBeGreaterThan(0, "optical route must exist for the crossing test");
        canvas.Connections.Add(new WaveguideConnectionViewModel(optical));

        return (canvas, electrical, optical);
    }

    private static Component CreateComponent(
        string nazcaFunction, string identifier, double x, double y, double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: nazcaFunction,
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = width,
            HeightMicrometers = height,
            PhysicalX = x,
            PhysicalY = y,
        };
    }

    private static PhysicalPin CreatePin(
        Component parent, string name, double offsetX, double offsetY, double angle, bool electrical)
    {
        var matterType = electrical ? MatterType.Electricity : MatterType.Light;
        return new PhysicalPin
        {
            Name = name,
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = angle,
            ParentComponent = parent,
            LogicalPin = new Pin(name, 0, matterType, RectSide.Right),
        };
    }
}
