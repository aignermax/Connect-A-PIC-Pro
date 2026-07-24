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

public class WaveguideConnectionFreezeTests
{
    [Fact]
    public void RecalculateTransmission_FrozenWithMatchingEndpoints_KeepsPath()
    {
        var (conn, router, _) = CreateRoutedConnection();
        var originalPath = conn.RoutedPath;

        conn.IsRouteFrozen = true;
        conn.RecalculateTransmission(router);

        conn.RoutedPath.ShouldBeSameAs(originalPath);
        conn.IsRouteFrozen.ShouldBeTrue();
    }

    [Fact]
    public void RecalculateTransmission_FrozenButEndpointMoved_UnfreezesAndReroutes()
    {
        var (conn, router, endComponent) = CreateRoutedConnection();
        conn.IsRouteFrozen = true;
        conn.BendRadiusOverrides[0] = 25;

        endComponent.PhysicalX += 500;
        conn.RecalculateTransmission(router);

        conn.IsRouteFrozen.ShouldBeFalse();
        conn.BendRadiusOverrides.ShouldBeEmpty();
        conn.RoutedPath.ShouldNotBeNull();
        // The new path must end at the moved pin.
        var (endX, _) = conn.EndPin.GetAbsolutePosition();
        conn.RoutedPath!.Segments[^1].EndPoint.X.ShouldBe(endX, 1.5);
    }

    [Fact]
    public void FrozenPathStillMatchesPins_MatchingEndpoints_ReturnsTrue()
    {
        var (conn, _, _) = CreateRoutedConnection();

        conn.FrozenPathStillMatchesPins().ShouldBeTrue();
    }

    [Fact]
    public void FrozenPathStillMatchesPins_MovedEndpoint_ReturnsFalse()
    {
        var (conn, _, endComponent) = CreateRoutedConnection();

        endComponent.PhysicalX += 500;

        conn.FrozenPathStillMatchesPins().ShouldBeFalse();
    }

    [Fact]
    public void FrozenPathStillMatchesPins_NoPath_ReturnsFalse()
    {
        var conn = new WaveguideConnection();

        conn.FrozenPathStillMatchesPins().ShouldBeFalse();
    }

    private static (WaveguideConnection Connection, WaveguideRouter Router, Component EndComponent)
        CreateRoutedConnection()
    {
        var startComponent = CreateTestComponent(0, 0);
        var endComponent = CreateTestComponent(200, 0);

        var conn = new WaveguideConnection
        {
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

        var router = new WaveguideRouter();
        conn.RecalculateTransmission(router);
        conn.RoutedPath.ShouldNotBeNull();
        return (conn, router, endComponent);
    }

    private static Component CreateTestComponent(double x, double y)
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
            identifier: $"TestComponent_{x}_{y}",
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
