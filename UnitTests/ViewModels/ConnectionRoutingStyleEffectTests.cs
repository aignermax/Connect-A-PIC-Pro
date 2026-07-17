using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Proves that picking a routing style for a selected connection through the same path the UI
/// uses — setting <see cref="ConnectionRoutingViewModel.SelectedStyle"/>, which triggers
/// <c>RecalculateRoutesAsync</c> — immediately reshapes the connection's <c>RoutedPath</c> into
/// the styled primitive geometry, WITHOUT moving any component. Uses offset (non-collinear) pins
/// so the styled curve is geometrically distinct from the automatic A* route.
/// </summary>
public class ConnectionRoutingStyleEffectTests
{
    private const double GeometryTolerance = 0.5;

    [Theory]
    [InlineData(WaveguideType.Straight)]
    [InlineData(WaveguideType.Bend)]
    [InlineData(WaveguideType.SBend)]
    public async Task SettingStyle_RebuildsRoutedPathAsStyledPrimitive_WithoutMovingComponents(WaveguideType style)
    {
        var canvas = new DesignCanvasViewModel();

        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.WidthMicrometers = 250;
        startComp.HeightMicrometers = 250;
        startComp.PhysicalX = 0;
        startComp.PhysicalY = 0;

        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.WidthMicrometers = 250;
        endComp.HeightMicrometers = 250;
        endComp.PhysicalX = 400;
        endComp.PhysicalY = 300; // offset in Y so Straight/Bend/SBend differ from the A* route

        canvas.AddComponent(startComp);
        canvas.AddComponent(endComp);

        var startPin = startComp.PhysicalPins.First(p => p.Name == "out");
        var endPin = endComp.PhysicalPins.First(p => p.Name == "in");

        var connVm = await canvas.ConnectPinsAsync(startPin, endPin);
        connVm.ShouldNotBeNull();
        connVm!.Connection.Type.ShouldBe(WaveguideType.Auto);
        connVm.Connection.RoutedPath.ShouldNotBeNull("the connection should have an automatic route to start with.");

        // Act — drive exactly what the routing panel does: select the connection, pick a style.
        var routingVm = new ConnectionRoutingViewModel(canvas);
        routingVm.SelectedConnection = connVm;
        routingVm.SelectedStyle = style; // fires RecalculateRoutesAsync internally
        await canvas.RecalculateRoutesAsync(); // await a full pass for a deterministic result

        // Assert — the connection now carries the chosen style, is frozen, and its geometry is
        // exactly the styled primitive the builder produces for these pins.
        connVm.Connection.Type.ShouldBe(style);
        connVm.Connection.IsRouteFrozen.ShouldBeTrue();

        // The styled route derives its curve entirely from the pin geometry (generous radius
        // for arc styles, sampled sine/Hermite for the smooth styles) — the InterconnectSettings
        // export defaults are deliberately not stamped onto it.
        var expected = ConnectionStyleRouteBuilder.Build(startPin, endPin, style);

        var actual = connVm.Connection.RoutedPath;
        actual.ShouldNotBeNull();
        actual!.Segments.Count.ShouldBe(expected.Segments.Count);
        for (int i = 0; i < expected.Segments.Count; i++)
        {
            actual.Segments[i].StartPoint.X.ShouldBe(expected.Segments[i].StartPoint.X, GeometryTolerance);
            actual.Segments[i].StartPoint.Y.ShouldBe(expected.Segments[i].StartPoint.Y, GeometryTolerance);
            actual.Segments[i].EndPoint.X.ShouldBe(expected.Segments[i].EndPoint.X, GeometryTolerance);
            actual.Segments[i].EndPoint.Y.ShouldBe(expected.Segments[i].EndPoint.Y, GeometryTolerance);
        }
    }

    [Fact]
    public async Task SwitchingBackToAuto_RestoresCollisionAvoidingRoute()
    {
        var canvas = new DesignCanvasViewModel();

        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.WidthMicrometers = 250;
        startComp.HeightMicrometers = 250;
        startComp.PhysicalX = 0;
        startComp.PhysicalY = 0;

        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.WidthMicrometers = 250;
        endComp.HeightMicrometers = 250;
        endComp.PhysicalX = 400;
        endComp.PhysicalY = 300;

        canvas.AddComponent(startComp);
        canvas.AddComponent(endComp);

        var startPin = startComp.PhysicalPins.First(p => p.Name == "out");
        var endPin = endComp.PhysicalPins.First(p => p.Name == "in");

        var connVm = await canvas.ConnectPinsAsync(startPin, endPin);
        connVm.ShouldNotBeNull();

        var routingVm = new ConnectionRoutingViewModel(canvas);
        routingVm.SelectedConnection = connVm;
        var originalRadius = connVm!.Connection.BendRadiusMicrometers;

        routingVm.SelectedStyle = WaveguideType.SBend;
        await canvas.RecalculateRoutesAsync();
        connVm.Connection.IsRouteFrozen.ShouldBeTrue();

        // Back to Auto: the frozen styled route must be released and re-routed automatically.
        routingVm.SelectedStyle = WaveguideType.Auto;
        await canvas.RecalculateRoutesAsync();

        connVm.Connection.Type.ShouldBe(WaveguideType.Auto);
        connVm.Connection.IsRouteFrozen.ShouldBeFalse();

        // Regression: styling must not stamp the 50 µm interconnect EXPORT default onto the
        // connection — that fed the A* router's minimum bend radius after returning to Auto
        // and produced unusably wide, overlapping routes.
        connVm.Connection.BendRadiusMicrometers.ShouldBe(originalRadius,
            "switching styles must not change the connection's own bend radius");
        connVm.Connection.RoutedPath.ShouldNotBeNull();

        // The automatic route arrives AT the end pin, unlike the styled Straight/SBend primitive.
        var (endX, endY) = endPin.GetAbsolutePosition();
        var last = connVm.Connection.RoutedPath!.Segments[^1];
        last.EndPoint.X.ShouldBe(endX, 1.0);
        last.EndPoint.Y.ShouldBe(endY, 1.0);
    }
}
