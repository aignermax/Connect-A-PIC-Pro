using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Tests for the per-connection process bend-radius floor (issue #937):
/// <see cref="WaveguideRouter.ConnectionProcessFloorProvider"/> resolves the floor from the
/// connection's endpoint pins, so on a multi-process canvas (a Cornerstone SiN chiplet at
/// 30 µm next to a SiEPIC SOI chiplet at 5 µm) each connection is floored by its own
/// chiplet's process. The stricter chiplet is no longer under-enforced by one canvas-wide
/// value, the looser one is no longer over-constrained, and
/// <see cref="RoutedPath.ViolatesProcessMinBendRadius"/> means "below THIS chiplet's
/// process minimum". A null provider result keeps the canvas-wide
/// <see cref="WaveguideRouter.ProcessMinBendRadiusMicrometers"/>.
/// </summary>
public class PerConnectionProcessFloorTests
{
    private const double CornerstoneSinMinRadius = 30.0;
    private const double SiepicSoiMinRadius = 5.0;
    private const double PlaygroundFallback = 10.0;

    [Fact]
    public void ResolveProcessFloorFor_ProviderNotWired_ReturnsCanvasWideFloor()
    {
        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = CornerstoneSinMinRadius };
        var (startPin, endPin) = FacingPinPair();

        router.ResolveProcessFloorFor(startPin, endPin).ShouldBe(CornerstoneSinMinRadius);
    }

    [Fact]
    public void ResolveProcessFloorFor_ProviderReturnsNull_ReturnsCanvasWideFloor()
    {
        var router = new WaveguideRouter
        {
            ProcessMinBendRadiusMicrometers = CornerstoneSinMinRadius,
            ConnectionProcessFloorProvider = (_, _) => null,
        };
        var (startPin, endPin) = FacingPinPair();

        router.ResolveProcessFloorFor(startPin, endPin).ShouldBe(CornerstoneSinMinRadius);
    }

    [Fact]
    public void ResolveProcessFloorFor_ProviderReturnsValue_OverridesCanvasWideFloorBothWays()
    {
        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = PlaygroundFallback };
        var (startPin, endPin) = FacingPinPair();

        // Stricter than the canvas-wide value (Cornerstone on a playground canvas)…
        router.ConnectionProcessFloorProvider = (_, _) => CornerstoneSinMinRadius;
        router.ResolveProcessFloorFor(startPin, endPin).ShouldBe(CornerstoneSinMinRadius);

        // …and looser (SiEPIC below the generic 10 µm fallback).
        router.ConnectionProcessFloorProvider = (_, _) => SiepicSoiMinRadius;
        router.ResolveProcessFloorFor(startPin, endPin).ShouldBe(SiepicSoiMinRadius);
    }

    [Fact]
    public void TwoChipletCanvas_EachConnectionKeepsItsOwnChipletsFloor()
    {
        // The #937 finding: one canvas, a Cornerstone SiN chiplet pair and a SiEPIC SOI
        // chiplet pair, canvas-wide floor at the playground fallback (10 µm). Before the
        // fix the Cornerstone route was silently routed below its 30 µm foundry floor and
        // the SiEPIC route was over-constrained at 10 µm.
        var cornerstoneA = CreateTestComponent("Cornerstone_a", 0, 0);
        var cornerstoneB = CreateTestComponent("Cornerstone_b", 300, 100);
        var siepicA = CreateTestComponent("SiEPIC_a", 0, 400);
        var siepicB = CreateTestComponent("SiEPIC_b", 300, 500);

        // Direct-first is disabled: the styled candidates use generous fitting radii that
        // would mask which floor was applied — the A* pipeline builds arcs at exactly the
        // effective radius (same rationale as ProcessMinRadiusDegradationTests). The discrete
        // allowed-radii list is cleared for the same reason: with it, smoothing and the
        // post-routing BendRadiusUpsizer would grow the loose chiplet's bends past the
        // canvas-wide value and hide which floor governed.
        var router = new WaveguideRouter
        {
            ProcessMinBendRadiusMicrometers = PlaygroundFallback,
            PreferDirectStyledRoutes = false,
            AllowedBendRadii = new List<double>(),
            ConnectionProcessFloorProvider = (startPin, _) =>
                startPin.ParentComponent.Identifier.StartsWith("Cornerstone") ? CornerstoneSinMinRadius
                : startPin.ParentComponent.Identifier.StartsWith("SiEPIC") ? SiepicSoiMinRadius
                : null,
        };
        router.InitializePathfindingGrid(-100, -100, 600, 700,
            new Component[] { cornerstoneA, cornerstoneB, siepicA, siepicB });

        var manager = new WaveguideConnectionManager(router);
        var cornerstoneConnection = new WaveguideConnection
        {
            StartPin = CreatePin("output", cornerstoneA, offsetX: 50, angleDegrees: 0),
            EndPin = CreatePin("input", cornerstoneB, offsetX: 0, angleDegrees: 180),
        };
        var siepicConnection = new WaveguideConnection
        {
            BendRadiusMicrometers = SiepicSoiMinRadius,
            StartPin = CreatePin("output", siepicA, offsetX: 50, angleDegrees: 0),
            EndPin = CreatePin("input", siepicB, offsetX: 0, angleDegrees: 180),
        };
        manager.AddExistingConnection(cornerstoneConnection);
        manager.AddExistingConnection(siepicConnection);
        manager.RecalculateAllTransmissions();

        // Cornerstone: raised to its 30 µm foundry floor although the canvas-wide value is 10.
        var cornerstoneBends = cornerstoneConnection.GetPathSegments().OfType<BendSegment>().ToList();
        cornerstoneBends.ShouldNotBeEmpty();
        cornerstoneBends.ShouldAllBe(b => b.RadiusMicrometers >= CornerstoneSinMinRadius - 1e-6);
        cornerstoneConnection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeFalse();

        // SiEPIC: routed at its own 5 µm process minimum, not dragged up to the
        // canvas-wide 10 µm fallback or the neighbour's 30 µm.
        var siepicBends = siepicConnection.GetPathSegments().OfType<BendSegment>().ToList();
        siepicBends.ShouldNotBeEmpty();
        siepicBends.ShouldAllBe(b => b.RadiusMicrometers >= SiepicSoiMinRadius - 1e-6);
        siepicBends.ShouldContain(b => b.RadiusMicrometers < PlaygroundFallback - 1e-6);
        siepicConnection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeFalse();
    }

    [Fact]
    public void TightNeighbors_PerConnectionFloor_DegradesToConnectionRadiusWithViolationFlag()
    {
        // The violation flag now means "below THIS chiplet's process minimum": the floor
        // comes from the provider (30 µm), the canvas-wide value is 0.
        var connection = RouteTightNeighbors(
            new WaveguideRouter
            {
                ProcessMinBendRadiusMicrometers = 0.0,
                ConnectionProcessFloorProvider = (_, _) => CornerstoneSinMinRadius,
            });

        connection.IsPathValid.ShouldBeTrue();
        connection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeTrue();
        PathIntersectionDetector.HasSelfIntersection(connection.RoutedPath).ShouldBeFalse();

        var bends = connection.GetPathSegments().OfType<BendSegment>().ToList();
        bends.ShouldNotBeEmpty();
        bends.ShouldAllBe(b => b.RadiusMicrometers >= connection.BendRadiusMicrometers - 1e-6);
    }

    [Fact]
    public void TightNeighbors_ProviderHasNoOpinion_NoViolationFlag()
    {
        var connection = RouteTightNeighbors(
            new WaveguideRouter
            {
                ProcessMinBendRadiusMicrometers = 0.0,
                ConnectionProcessFloorProvider = (_, _) => null,
            });

        connection.IsPathValid.ShouldBeTrue();
        connection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeFalse();
    }

    [Fact]
    public void StyledBend_PerConnectionFloor_RaisesArcRadiusToTheFloor()
    {
        // Styled curves take the same per-connection floor (the WaveguideConnection
        // effective-radius path): a 29 µm floor still fits the 30 µm maximum here.
        var connection = new WaveguideConnection
        {
            Type = WaveguideType.Bend,
            StartPin = CreatePin("output", CreateTestComponent("styled_start", 0, 0), offsetX: 50, angleDegrees: 0),
            EndPin = CreatePin("input", CreateTestComponent("styled_end", 100, 30), offsetX: 0, angleDegrees: 270),
        };
        var router = new WaveguideRouter
        {
            ProcessMinBendRadiusMicrometers = 0.0,
            ConnectionProcessFloorProvider = (_, _) => 29.0,
        };

        connection.RecalculateTransmission(router);

        var bend = connection.GetPathSegments().OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(29.0, 0.01);
    }

    /// <summary>
    /// Two components whose facing pins are only ~40 µm apart in X and Y — too tight for a
    /// 30 µm bend radius (the degradation layout from <c>ProcessMinRadiusDegradationTests</c>).
    /// </summary>
    private static WaveguideConnection RouteTightNeighbors(WaveguideRouter router)
    {
        var left = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        left.PhysicalX = 60;
        left.PhysicalY = 60;
        var right = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        right.PhysicalX = 350;
        right.PhysicalY = 100;

        router.InitializePathfindingGrid(-100, -100, 1100, 900, new[] { left, right });

        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection
        {
            StartPin = left.PhysicalPins.First(p => p.Name == "out"),
            EndPin = right.PhysicalPins.First(p => p.Name == "in"),
        };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        return connection;
    }

    private static (PhysicalPin Start, PhysicalPin End) FacingPinPair()
    {
        var start = CreateTestComponent("pair_start", 0, 0);
        var end = CreateTestComponent("pair_end", 300, 100);
        return (CreatePin("output", start, offsetX: 50, angleDegrees: 0),
                CreatePin("input", end, offsetX: 0, angleDegrees: 180));
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

    private static Component CreateTestComponent(string identifier, double x, double y)
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
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
            PhysicalX = x,
            PhysicalY = y,
        };
    }
}
