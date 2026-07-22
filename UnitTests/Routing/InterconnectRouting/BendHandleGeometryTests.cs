using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Verifies the in-canvas bend-radius handle support: that a styled arc S-route (Bend with
/// parallel offset pins) exposes editable bend corners, and that the pointer→radius mapping
/// used by the drag round-trips. SBend/Cobra are smooth polylines without a single radius,
/// so they intentionally expose no handles — the arc styles are the handle carriers.
/// </summary>
public class BendHandleGeometryTests
{
    [Fact]
    public void GetBendCorners_StyledSRoute_ReturnsOneCornerPerArc()
    {
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 20);
        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);

        var corners = BendRadiusEditor.GetBendCorners(path.Segments);

        // The S is stub–arc–straight–arc–stub, so both arcs are interior and editable.
        corners.Count.ShouldBe(2);
        corners[0].BendIndex.ShouldBe(0);
        corners[1].BendIndex.ShouldBe(1);
        foreach (var corner in corners)
            corner.RadiusMicrometers.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void HandlePointAndProjection_RoundTripToRadius()
    {
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 20);
        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);
        var corner = BendRadiusEditor.GetBendCorners(path.Segments)[0];

        // The handle sits at Corner + Radius·HandleFactor·Bisector; projecting that point back
        // along the bisector and inverting the placement must recover the current radius.
        var handle = BendHandleGeometry.HandlePoint(corner);
        double distance = BendHandleGeometry.ProjectDistance(corner.Corner, corner.Bisector, handle);
        double recovered = BendHandleGeometry.RadiusFromDistance(distance, corner.HandleFactor);

        recovered.ShouldBe(corner.RadiusMicrometers, 1e-6);
    }

    [Fact]
    public void HandlePoint_LiesOnTheArc_ForNon90DegreeSweeps()
    {
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 20);
        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);
        var bends = path.Segments.OfType<BendSegment>().ToList();
        var corners = BendRadiusEditor.GetBendCorners(path.Segments);
        corners.Count.ShouldBe(bends.Count);

        for (int i = 0; i < corners.Count; i++)
        {
            // Guard: this fixture must exercise NON-90° sweeps — at exactly 90° sin equals cos,
            // so a wrong placement factor would still pass and the test would prove nothing.
            (Math.Abs(Math.Abs(bends[i].SweepAngleDegrees) - 90.0) > 5).ShouldBeTrue(
                $"fixture sweep was {bends[i].SweepAngleDegrees:F1}° — adjust the layout");

            // The handle is the arc's nearest point to the corner, so it must sit ON the arc:
            // exactly RadiusMicrometers away from the bend center.
            var handle = BendHandleGeometry.HandlePoint(corners[i]);
            double distToCenter = Math.Sqrt(
                Math.Pow(handle.X - bends[i].Center.X, 2) +
                Math.Pow(handle.Y - bends[i].Center.Y, 2));
            distToCenter.ShouldBe(bends[i].RadiusMicrometers, 0.01,
                "the drag handle must sit ON the arc, not float away from it");
        }
    }

    [Fact]
    public void DraggingHandleOutward_IncreasesBendRadius()
    {
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 20);
        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);
        conn.RestoreCachedPath(path);
        var corner = BendRadiusEditor.GetBendCorners(conn.GetPathSegments())[0];

        // Mapping: pushing the handle further out along the bisector means a larger radius.
        // (For shallow sweeps the factor is small, so tiny handle motion = big radius change —
        // physically correct; hence no fixed-µm apply here.)
        var handle = BendHandleGeometry.HandlePoint(corner);
        var pointer = (X: handle.X + 3 * corner.Bisector.X, Y: handle.Y + 3 * corner.Bisector.Y);
        double distance = BendHandleGeometry.ProjectDistance(corner.Corner, corner.Bisector, pointer);
        double newRadius = BendHandleGeometry.RadiusFromDistance(distance, corner.HandleFactor);
        newRadius.ShouldBeGreaterThan(corner.RadiusMicrometers);

        // Apply: a modestly smaller radius always fits on the flanking straights (the styled S
        // is built near the maximum radius, so growing has almost no headroom by design).
        double smaller = corner.RadiusMicrometers * 0.8;
        BendRadiusEditor.TryApplyOverride(conn, corner.BendIndex, smaller, out var error).ShouldBeTrue(error);
        var updated = (BendSegment)conn.GetPathSegments().First(s => s is BendSegment);
        updated.RadiusMicrometers.ShouldBe(smaller, 1e-6);
    }

    /// <summary>Mirrors the fixture in <c>ConnectionStyleRouteBuilderTests</c>.</summary>
    private static WaveguideConnection CreateConnection(
        WaveguideType type, double endOffsetY = 0, double endPinAngleDegrees = 180)
    {
        var startComponent = CreateTestComponent(0, 0);
        var endComponent = CreateTestComponent(100, endOffsetY);

        return new WaveguideConnection
        {
            Type = type,
            StartPin = new PhysicalPin
            {
                Name = "output",
                OffsetXMicrometers = 50,
                OffsetYMicrometers = 25,
                AngleDegrees = 0,
                ParentComponent = startComponent,
            },
            EndPin = new PhysicalPin
            {
                Name = "input",
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 25,
                AngleDegrees = endPinAngleDegrees,
                ParentComponent = endComponent,
            },
        };
    }

    private static Component CreateTestComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"TestComponent_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
            PhysicalX = x,
            PhysicalY = y,
        };
    }
}
