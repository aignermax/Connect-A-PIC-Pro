using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Integration regression tests replaying the geometry of the original repro
/// designs (overlappingwaveguides.lun / Kreisverbindung.lun): routed waveguides
/// of neighboring ports must never silently overlap, and no routed path may
/// cross itself (a 360° loop at the start pin).
/// </summary>
public class Issue704ReproRoutingTests
{
    /// <summary>Minimum clearance between two independent waveguides (µm).</summary>
    private const double MinClearanceMicrometers = 2.0;

    /// <summary>Taper pin o1 absolute position from the repro files.</summary>
    private const double TaperPinX = Issue704ReproCircuit.TaperPinX;

    /// <summary>Taper pin o1 absolute position from the repro files.</summary>
    private const double TaperPinY = Issue704ReproCircuit.TaperPinY;

    [Fact]
    public void OverlappingWaveguidesRepro_NeighboringPortRoutes_DoNotSilentlyOverlap()
    {
        // Geometry of overlappingwaveguides.lun: the Taper pin sits 5.9 µm below
        // MZI_9.o2's entry axis, so the second route's terminal approach used to
        // cut straight through the first waveguide.
        var mzi8 = Issue704ReproCircuit.CreateMzi("MZI_8", 374.34455820950575, 218.3565233418277);
        var mzi9 = Issue704ReproCircuit.CreateMzi("MZI_9", 236.5708507637589, 649.767215289101);
        var taper = Issue704ReproCircuit.CreateTaper("Taper_5", TaperPinX, TaperPinY);

        var manager = CreateRoutedManager(out _, mzi8, mzi9, taper);
        var conn1 = manager.AddConnection(
            Issue704ReproCircuit.Pin(mzi8, "o3"), Issue704ReproCircuit.Pin(mzi9, "o3"));
        var conn2 = manager.AddConnection(
            Issue704ReproCircuit.Pin(taper, "o1"), Issue704ReproCircuit.Pin(mzi9, "o2"));

        AssertNoSilentOverlap(conn1, conn2);
    }

    [Fact]
    public void KreisverbindungRepro_NoRouteIsSelfIntersecting()
    {
        // Geometry of Kreisverbindung.lun: the start pin points away from the goal
        // and the A* search used to return a full 360° circle crossing itself at
        // the start pin.
        var mzi8 = Issue704ReproCircuit.CreateMzi("MZI_8", 382.34332648799983, 368.7961620369691);
        var mzi9 = Issue704ReproCircuit.CreateMzi("MZI_9", 154.34028802348783, 772.173225746585);
        var taper = Issue704ReproCircuit.CreateTaper("Taper_5", TaperPinX, TaperPinY);

        var manager = CreateRoutedManager(out _, mzi8, mzi9, taper);
        manager.AddConnection(
            Issue704ReproCircuit.Pin(mzi8, "o3"), Issue704ReproCircuit.Pin(mzi9, "o3"));
        manager.AddConnection(
            Issue704ReproCircuit.Pin(taper, "o1"), Issue704ReproCircuit.Pin(mzi9, "o2"));

        foreach (var connection in manager.Connections)
        {
            var path = connection.RoutedPath;
            path.ShouldNotBeNull($"route {connection} produced no path at all");

            // Engine-agnostic invariant: the smoothed waveguide geometry must
            // never cross itself, no matter which routing engine built it.
            PathIntersectionDetector.HasSelfIntersection(path).ShouldBeFalse(
                $"route {connection} must never cross itself");

            // Grid-level invariant for A*-routed connections: no grid cell may
            // be visited twice by the path body (the 360° loop symptom).
            if (path.DebugGridPath != null)
            {
                PathLoopDetector.IsSelfIntersecting(path.DebugGridPath).ShouldBeFalse(
                    $"route {connection} visits a grid cell twice");
            }
        }
    }

    [Fact]
    public void CouplerStraightBackConnections_NoRouteSelfIntersects()
    {
        // Two coupler_straight instances stacked almost on top of each other
        // (Δx ≈ 1.2 µm, Δy ≈ 13.1 µm) with three back-connections whose pins face
        // away from their target. These pathologically tight U-turns made the router
        // emit self-crossing loops that were flagged invalid yet still exported.
        var coupler1 = Issue704ReproCircuit.CreateCouplerStraight("Coupler_Straight_1", 345.698, -497.001);
        var coupler2 = Issue704ReproCircuit.CreateCouplerStraight("Coupler_Straight_2", 344.483, -483.894);

        var manager = CreateRoutedManager(
            out _, minX: 200, minY: -640, maxX: 520, maxY: -340, coupler1, coupler2);
        manager.AddConnection(
            Issue704ReproCircuit.Pin(coupler2, "o4"), Issue704ReproCircuit.Pin(coupler1, "o3"));
        manager.AddConnection(
            Issue704ReproCircuit.Pin(coupler1, "o4"), Issue704ReproCircuit.Pin(coupler2, "o3"));
        manager.AddConnection(
            Issue704ReproCircuit.Pin(coupler2, "o1"), Issue704ReproCircuit.Pin(coupler1, "o2"));

        foreach (var connection in manager.Connections)
        {
            var path = connection.RoutedPath;
            path.ShouldNotBeNull();
            PathIntersectionDetector.HasSelfIntersection(path).ShouldBeFalse(
                $"route {connection} must never cross itself");
            path.IsInvalidGeometry.ShouldBeFalse(
                $"route {connection} must not be left as invalid geometry");
        }
    }

    /// <summary>
    /// Creates a router + connection manager over the repro components, using
    /// the same 4-direction routing configuration as the original designs.
    /// </summary>
    private static WaveguideConnectionManager CreateRoutedManager(
        out WaveguideRouter router, params CAP_Core.Components.Core.Component[] components) =>
        CreateRoutedManager(out router, 0, 0, 1200, 1000, components);

    /// <summary>
    /// Creates a router + connection manager with an explicit grid extent, so layouts
    /// placed at negative coordinates (exported netlist positions) are covered.
    /// </summary>
    private static WaveguideConnectionManager CreateRoutedManager(
        out WaveguideRouter router, double minX, double minY, double maxX, double maxY,
        params CAP_Core.Components.Core.Component[] components)
    {
        router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 10.0,
            AStarCellSize = 4.0,
            UseDiagonalRouting = false,
        };
        router.InitializePathfindingGrid(minX, minY, maxX, maxY, components.ToList());
        return new WaveguideConnectionManager(router);
    }

    /// <summary>
    /// Asserts the no-silent-overlap invariant: two independently routed waveguides either
    /// keep a physical clearance, or the collision is explicitly flagged
    /// (blocked fallback / invalid geometry) — never a silent overlap.
    /// </summary>
    private static void AssertNoSilentOverlap(WaveguideConnection a, WaveguideConnection b)
    {
        a.RoutedPath.ShouldNotBeNull();
        b.RoutedPath.ShouldNotBeNull();
        a.RoutedPath.Segments.ShouldNotBeEmpty();
        b.RoutedPath.Segments.ShouldNotBeEmpty();

        bool flagged = a.RoutedPath.IsBlockedFallback || a.RoutedPath.IsInvalidGeometry
            || b.RoutedPath.IsBlockedFallback || b.RoutedPath.IsInvalidGeometry;
        if (flagged) return;

        double minDistance = Issue704ReproCircuit.MinDistanceBetween(
            Issue704ReproCircuit.SamplePath(a.RoutedPath),
            Issue704ReproCircuit.SamplePath(b.RoutedPath));
        minDistance.ShouldBeGreaterThan(MinClearanceMicrometers,
            "unflagged waveguides must not run through each other");
    }
}
