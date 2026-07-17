using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
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
/// A bend radius edited via the canvas handles on a STYLED route (Type != Auto) is a manual
/// edit and must survive subsequent recalculations while the pins stay put — the styled
/// branch of <see cref="WaveguideConnection.RecalculateTransmission"/> must not silently
/// rebuild over it. Moving an endpoint rebuilds and discards the edit, mirroring Auto.
/// </summary>
public class StyledRouteHandleEditPersistenceTests
{
    private const double DefaultRadius = 10.0;
    private const double EditedRadius = 6.0;

    [Fact]
    public void HandleEdit_OnStyledRoute_SurvivesRecalc_WhilePinsUnchanged()
    {
        var conn = CreateStyledArcSConnection();
        var router = new WaveguideRouter();
        conn.RecalculateTransmission(router);

        var corners = BendRadiusEditor.GetBendCorners(conn.GetPathSegments());
        corners.ShouldNotBeEmpty("styled S route must expose editable bend corners");
        BendRadiusEditor.TryApplyOverride(conn, corners[0].BendIndex, EditedRadius, out var error)
            .ShouldBeTrue(error);

        // Any later recalculation (e.g. another component moved elsewhere) with unchanged pins:
        conn.RecalculateTransmission(router);

        conn.BendRadiusOverrides.ShouldContainKey(corners[0].BendIndex);
        var editedBend = FindBend(conn, corners[0].BendIndex);
        editedBend.RadiusMicrometers.ShouldBe(EditedRadius, 0.01,
            "the manual handle edit must survive a recalc while pins are unchanged");
    }

    [Fact]
    public void HandleEdit_OnStyledRoute_IsDiscarded_WhenEndpointMoves()
    {
        var conn = CreateStyledArcSConnection();
        var router = new WaveguideRouter();
        conn.RecalculateTransmission(router);

        var corners = BendRadiusEditor.GetBendCorners(conn.GetPathSegments());
        corners.ShouldNotBeEmpty();
        BendRadiusEditor.TryApplyOverride(conn, corners[0].BendIndex, EditedRadius, out _)
            .ShouldBeTrue();

        // Endpoint moved: the styled primitive is rebuilt from the new pins, edits discarded.
        conn.EndPin!.ParentComponent!.PhysicalX += 40;
        conn.RecalculateTransmission(router);

        conn.BendRadiusOverrides.ShouldBeEmpty(
            "moving an endpoint rebuilds the styled route and discards manual bend edits");
        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        var last = conn.GetPathSegments()[^1];
        last.EndPoint.X.ShouldBe(endX, 0.5);
        last.EndPoint.Y.ShouldBe(endY, 0.5);
    }

    /// <summary>Parallel offset pins with the Bend style: the styled route is the two-arc S,
    /// which carries the in-canvas radius handles (SBend/Cobra are handle-less polylines).</summary>
    private static WaveguideConnection CreateStyledArcSConnection()
    {
        var startComponent = CreateTestComponent(0, 0);
        var endComponent = CreateTestComponent(100, 30);

        return new WaveguideConnection
        {
            Type = WaveguideType.Bend,
            BendRadiusMicrometers = DefaultRadius,
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
                AngleDegrees = 180,
                ParentComponent = endComponent,
            },
        };
    }

    private static BendSegment FindBend(WaveguideConnection conn, int bendIndex)
    {
        int seen = -1;
        foreach (var segment in conn.GetPathSegments())
        {
            if (segment is BendSegment bend && ++seen == bendIndex)
                return bend;
        }
        throw new InvalidOperationException($"Bend #{bendIndex} not found.");
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
