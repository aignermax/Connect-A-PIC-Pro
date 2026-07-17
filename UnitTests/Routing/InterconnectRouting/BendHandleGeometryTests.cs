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
/// Verifies the in-canvas bend-radius handle support: that a styled S-route exposes editable
/// bend corners, and that the pointer→radius mapping used by the drag round-trips.
/// </summary>
public class BendHandleGeometryTests
{
    private const double Radius = 10.0;

    [Fact]
    public void GetBendCorners_StyledSRoute_ReturnsOneCornerPerArc()
    {
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 20);
        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

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
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 20);
        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);
        var corner = BendRadiusEditor.GetBendCorners(path.Segments)[0];

        // The handle sits at Corner + Radius·HandleFactor·Bisector; projecting that point back
        // along the bisector and inverting the placement must recover the current radius.
        var handle = BendHandleGeometry.HandlePoint(corner);
        double distance = BendHandleGeometry.ProjectDistance(corner.Corner, corner.Bisector, handle);
        double recovered = BendHandleGeometry.RadiusFromDistance(distance, corner.HandleFactor);

        recovered.ShouldBe(corner.RadiusMicrometers, 1e-6);
    }

    [Fact]
    public void DraggingHandleOutward_IncreasesBendRadius()
    {
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 20);
        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);
        conn.RestoreCachedPath(path);
        var corner = BendRadiusEditor.GetBendCorners(conn.GetPathSegments())[0];

        // Simulate a drag that pushes the handle 3 µm further out along the bisector.
        var handle = BendHandleGeometry.HandlePoint(corner);
        var pointer = (X: handle.X + 3 * corner.Bisector.X, Y: handle.Y + 3 * corner.Bisector.Y);
        double distance = BendHandleGeometry.ProjectDistance(corner.Corner, corner.Bisector, pointer);
        double newRadius = BendHandleGeometry.RadiusFromDistance(distance, corner.HandleFactor);

        newRadius.ShouldBeGreaterThan(corner.RadiusMicrometers);
        BendRadiusEditor.TryApplyOverride(conn, corner.BendIndex, newRadius, out var error).ShouldBeTrue(error);
        var updated = (BendSegment)conn.GetPathSegments().First(s => s is BendSegment);
        updated.RadiusMicrometers.ShouldBe(newRadius, 1e-6);
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
