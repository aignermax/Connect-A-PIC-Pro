using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.MetalRouting;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MetalRouting;

/// <summary>
/// Tests the process metal bend-radius floor (issue #854): electrical pin pairs are
/// routed with <see cref="WaveguideRouter.MetalProcessMinBendRadiusMicrometers"/> as
/// the radius floor while optical pairs keep the optical floor, and electrical
/// connections register grid obstacles at the metal trace width.
/// </summary>
public class MetalBendRadiusRoutingTests
{
    private const double RadiusTolerance = 1e-6;

    [Fact]
    public void Route_ElectricalPins_BendsUseMetalFloorRadius()
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 10,
            ProcessMinBendRadiusMicrometers = 5,
            MetalProcessMinBendRadiusMicrometers = 40,
        };

        var path = router.Route(
            CreatePin(MatterType.Electricity, 0, 0, pinX: 50, pinY: 25, angle: 0),
            CreatePin(MatterType.Electricity, 200, 100, pinX: 0, pinY: 25, angle: 180));

        var bends = path.Segments.OfType<BendSegment>().ToList();
        bends.ShouldNotBeEmpty();
        bends.ShouldAllBe(b => Math.Abs(b.RadiusMicrometers - 40) < RadiusTolerance);
    }

    [Fact]
    public void Route_OpticalPins_MetalFloorDoesNotApply()
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 10,
            ProcessMinBendRadiusMicrometers = 0,
            MetalProcessMinBendRadiusMicrometers = 40,
        };

        var path = router.Route(
            CreatePin(MatterType.Light, 0, 0, pinX: 50, pinY: 25, angle: 0),
            CreatePin(MatterType.Light, 200, 100, pinX: 0, pinY: 25, angle: 180));

        var bends = path.Segments.OfType<BendSegment>().ToList();
        bends.ShouldNotBeEmpty();
        bends.ShouldAllBe(b => Math.Abs(b.RadiusMicrometers - 10) < RadiusTolerance);
    }

    [Fact]
    public void Route_ConnectionRadiusAboveMetalFloor_ConnectionRadiusWins()
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 60,
            MetalProcessMinBendRadiusMicrometers = 40,
        };

        var path = router.Route(
            CreatePin(MatterType.Electricity, 0, 0, pinX: 50, pinY: 25, angle: 0),
            CreatePin(MatterType.Electricity, 400, 200, pinX: 0, pinY: 25, angle: 180));

        var bends = path.Segments.OfType<BendSegment>().ToList();
        bends.ShouldNotBeEmpty();
        bends.ShouldAllBe(b => Math.Abs(b.RadiusMicrometers - 60) < RadiusTolerance);
    }

    [Fact]
    public void ObstacleWidthFor_ElectricalConnection_UsesMetalTraceWidth()
    {
        var manager = new WaveguideConnectionManager(new WaveguideRouter())
        {
            WaveguideWidthMicrometers = 4,
            MetalTraceWidthMicrometers = 12,
        };
        var connection = new WaveguideConnection
        {
            StartPin = CreatePin(MatterType.Electricity, 0, 0, 0, 0, 0),
            EndPin = CreatePin(MatterType.Electricity, 100, 0, 0, 0, 180),
        };

        manager.ObstacleWidthFor(connection).ShouldBe(12);
    }

    [Fact]
    public void ObstacleWidthFor_OpticalConnection_UsesWaveguideWidth()
    {
        var manager = new WaveguideConnectionManager(new WaveguideRouter())
        {
            WaveguideWidthMicrometers = 4,
            MetalTraceWidthMicrometers = 12,
        };
        var connection = new WaveguideConnection
        {
            StartPin = CreatePin(MatterType.Light, 0, 0, 0, 0, 0),
            EndPin = CreatePin(MatterType.Light, 100, 0, 0, 0, 180),
        };

        manager.ObstacleWidthFor(connection).ShouldBe(4);
    }

    [Fact]
    public void MetalTraceWidth_Default_MatchesMetalRoutingSpecDefault()
    {
        var manager = new WaveguideConnectionManager(new WaveguideRouter());

        manager.MetalTraceWidthMicrometers.ShouldBe(MetalRoutingSpec.DefaultTraceWidthMicrometers);
    }

    private static PhysicalPin CreatePin(
        MatterType matterType, double componentX, double componentY,
        double pinX, double pinY, double angle)
    {
        return new PhysicalPin
        {
            Name = "p",
            OffsetXMicrometers = pinX,
            OffsetYMicrometers = pinY,
            AngleDegrees = angle,
            ParentComponent = CreateComponent(componentX, componentY),
            LogicalPin = new Pin("p", 0, matterType, RectSide.Right),
        };
    }

    private static Component CreateComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"test_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
            PhysicalX = x,
            PhysicalY = y,
        };
        return component;
    }
}
