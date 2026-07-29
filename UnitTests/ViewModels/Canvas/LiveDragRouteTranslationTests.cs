using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Canvas;

/// <summary>
/// Tests for issue #805 (live-drag half): while BOTH connected components are dragged
/// together, the waveguide between them must follow the pointer instead of staying pinned
/// to its old grid position until drop. A joint drag is a pure translation, so the route is
/// shifted by the same per-frame delta — the same behaviour a Ctrl+G group already has.
/// Connections that only have ONE endpoint in the selection are left for the drop-time
/// re-route (their geometry genuinely changes).
/// </summary>
public class LiveDragRouteTranslationTests
{
    private const double DeltaX = 40.0;
    private const double DeltaY = -25.0;
    private const double Tolerance = 1e-9;

    [Fact]
    public void TranslateInternalConnectionRoutes_BothEndpointsSelected_ShiftsRouteByDelta()
    {
        var canvas = new DesignCanvasViewModel();
        var comp1 = CreateComponentWithPins(100, 100);
        var comp2 = CreateComponentWithPins(300, 100);
        var vm1 = canvas.AddComponent(comp1);
        var vm2 = canvas.AddComponent(comp2);

        var connVm = canvas.ConnectPinsWithCachedRoute(
            comp1.PhysicalPins[1], comp2.PhysicalPins[0], StraightRoute(150, 125, 300, 125));
        connVm.ShouldNotBeNull();
        var originalStart = connVm.Connection.RoutedPath!.Segments[0].StartPoint;
        var originalEnd = connVm.Connection.RoutedPath!.Segments[0].EndPoint;

        canvas.TranslateInternalConnectionRoutes(new[] { vm1, vm2 }, DeltaX, DeltaY);

        var seg = connVm.Connection.RoutedPath!.Segments[0];
        seg.StartPoint.X.ShouldBe(originalStart.X + DeltaX, Tolerance);
        seg.StartPoint.Y.ShouldBe(originalStart.Y + DeltaY, Tolerance);
        seg.EndPoint.X.ShouldBe(originalEnd.X + DeltaX, Tolerance);
        seg.EndPoint.Y.ShouldBe(originalEnd.Y + DeltaY, Tolerance);
    }

    [Fact]
    public void TranslateInternalConnectionRoutes_OnlyOneEndpointSelected_LeavesRouteUntouched()
    {
        var canvas = new DesignCanvasViewModel();
        var comp1 = CreateComponentWithPins(100, 100);
        var comp2 = CreateComponentWithPins(300, 100);
        var vm1 = canvas.AddComponent(comp1);
        var vm2 = canvas.AddComponent(comp2);

        var connVm = canvas.ConnectPinsWithCachedRoute(
            comp1.PhysicalPins[1], comp2.PhysicalPins[0], StraightRoute(150, 125, 300, 125));
        connVm.ShouldNotBeNull();
        var originalPath = connVm.Connection.RoutedPath;

        canvas.TranslateInternalConnectionRoutes(new[] { vm2 }, DeltaX, DeltaY);

        connVm.Connection.RoutedPath.ShouldBeSameAs(originalPath,
            "a connection with only one moved endpoint must re-route on drop, not translate");
    }

    private static RoutedPath StraightRoute(double x1, double y1, double x2, double y2)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        return path;
    }

    private static Component CreateComponentWithPins(double x, double y)
    {
        const double width = 100, height = 50;
        var physicalPins = new List<PhysicalPin>
        {
            new() { Name = "west0", OffsetXMicrometers = 0, OffsetYMicrometers = height / 2 },
            new() { Name = "east0", OffsetXMicrometers = width, OffsetYMicrometers = height / 2 },
        };

        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>
        {
            new("west0", 0, MatterType.Light, RectSide.Left),
            new("east0", 1, MatterType.Light, RectSide.Right),
        });

        return new Component(
            new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            parts,
            0,
            "TestComp",
            DiscreteRotation.R0,
            physicalPins)
        {
            WidthMicrometers = width,
            HeightMicrometers = height,
            PhysicalX = x,
            PhysicalY = y,
        };
    }
}
