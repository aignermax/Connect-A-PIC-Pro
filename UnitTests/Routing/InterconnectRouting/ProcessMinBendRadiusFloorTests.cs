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
/// Verifies that the fabrication process' minimum bend radius
/// (<see cref="WaveguideRouter.ProcessMinBendRadiusMicrometers"/>, e.g. 30 µm for the
/// Cornerstone SiN process) floors the effective bend radius everywhere: the AUTO (A*)
/// route, the Manhattan fallback, and the styled Bend/S geometry. The per-connection
/// radius and the process minimum combine via max — the larger one governs. When the
/// floor cannot be realized geometrically, the router degrades to the connection radius
/// and flags the route (see <c>ProcessMinRadiusDegradationTests</c>).
/// </summary>
public class ProcessMinBendRadiusFloorTests
{
    private const double CornerstoneSinMinRadius = 30.0;

    [Fact]
    public void Auto_ProcessMinimum30_AllBendsAtLeast30Micrometers()
    {
        var (router, connection) = CreateAutoLayout(offsetY: 100);
        router.ProcessMinBendRadiusMicrometers = CornerstoneSinMinRadius;

        connection.RecalculateTransmission(router);

        // With enough room the router must realize the floor — no degradation flag.
        var bends = connection.GetPathSegments().OfType<BendSegment>().ToList();
        bends.ShouldNotBeEmpty();
        bends.ShouldAllBe(b => b.RadiusMicrometers >= CornerstoneSinMinRadius - 1e-6);
        connection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeFalse();
    }

    [Fact]
    public void Auto_NoProcessMinimum_ConnectionDefault10Governs()
    {
        var (router, connection) = CreateAutoLayout(offsetY: 100);

        connection.RecalculateTransmission(router);

        router.MinBendRadiusMicrometers.ShouldBe(10.0);
    }

    [Fact]
    public void Auto_ConnectionRadiusAboveProcessMinimum_WinsByMaxSemantics()
    {
        var (router, connection) = CreateAutoLayout(offsetY: 100);
        router.ProcessMinBendRadiusMicrometers = CornerstoneSinMinRadius;
        connection.BendRadiusMicrometers = 40.0;

        connection.RecalculateTransmission(router);

        router.MinBendRadiusMicrometers.ShouldBe(40.0);
    }

    [Fact]
    public void StyledBend_ProcessMinimumWithinFit_RaisesArcRadiusToTheFloor()
    {
        // Angled 90°-turn layout with legs 50/30 µm: largest fitting radius 30 µm, generous
        // default 27 µm. A 29 µm floor still fits, so the arc uses exactly the floor.
        var connection = CreateStyledBendConnection();
        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = 29.0 };

        connection.RecalculateTransmission(router);

        var bend = connection.GetPathSegments().OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(29.0, 0.01);
    }

    [Fact]
    public void StyledBend_ProcessMinimumBeyondFit_KeepsGenerousFittingRadius()
    {
        // A 100 µm floor cannot fit the 30 µm maximum — the fitting clamp keeps governing
        // (the documented styled-route exception) instead of breaking the geometry.
        var connection = CreateStyledBendConnection();
        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = 100.0 };

        connection.RecalculateTransmission(router);

        var bend = connection.GetPathSegments().OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(30.0 * SBendGeometry.GenerousRadiusFactor, 0.01);
    }

    [Theory]
    [InlineData(0.0, 27.0)]   // no floor → generous default
    [InlineData(20.0, 27.0)]  // floor below generous → generous stays (max semantics)
    [InlineData(29.0, 29.0)]  // floor between generous and fit → floor wins
    [InlineData(31.0, 27.0)]  // floor beyond the fit → fitting radius keeps governing
    public void ApplyRadiusFloor_CombinesGenerousDefaultAndFloor(double floor, double expected)
    {
        SBendGeometry.ApplyRadiusFloor(30.0, floor).ShouldBe(expected, 1e-9);
    }

    /// <summary>
    /// Two components with laterally offset facing pins and an initialized A* grid, so the
    /// AUTO route must contain bends. The connection keeps its 10 µm default radius.
    /// </summary>
    private static (WaveguideRouter Router, WaveguideConnection Connection) CreateAutoLayout(double offsetY)
    {
        var startComponent = CreateTestComponent(0, 0);
        var endComponent = CreateTestComponent(300, offsetY);
        var router = new WaveguideRouter();
        router.InitializePathfindingGrid(-100, -100, 500, 300, new[] { startComponent, endComponent });

        var connection = new WaveguideConnection
        {
            StartPin = CreatePin("output", startComponent, offsetX: 50, angleDegrees: 0),
            EndPin = CreatePin("input", endComponent, offsetX: 0, angleDegrees: 180),
        };
        return (router, connection);
    }

    /// <summary>
    /// Styled Bend connection with a 90° turn: start pin at (50, 25) heading 0°, end pin at
    /// (100, 55) heading 270° — corner (100, 25), legs 50 µm and 30 µm (matches
    /// <c>ConnectionStyleRouteBuilderTests.Bend_AngledPins_BuildsStubArcStub_WithGenerousRadius</c>).
    /// </summary>
    private static WaveguideConnection CreateStyledBendConnection()
    {
        return new WaveguideConnection
        {
            Type = WaveguideType.Bend,
            StartPin = CreatePin("output", CreateTestComponent(0, 0), offsetX: 50, angleDegrees: 0),
            EndPin = CreatePin("input", CreateTestComponent(100, 30), offsetX: 0, angleDegrees: 270),
        };
    }

    private static PhysicalPin CreatePin(string name, Component parent, double offsetX, double angleDegrees)
    {
        return new PhysicalPin
        {
            Name = name,
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = 25,
            AngleDegrees = angleDegrees,
            ParentComponent = parent,
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
