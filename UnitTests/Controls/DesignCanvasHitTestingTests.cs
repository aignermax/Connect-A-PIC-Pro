using Avalonia;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;

namespace UnitTests.Controls;

/// <summary>
/// Unit tests for <see cref="DesignCanvasHitTesting"/>.
/// Verifies component, pin, and connection hit-test logic.
/// </summary>
public class DesignCanvasHitTestingTests
{
    [Fact]
    public void HitTestComponent_ReturnsNull_WhenViewModelIsNull()
    {
        var result = DesignCanvasHitTesting.HitTestComponent(new Point(0, 0), null);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestComponent_ReturnsNull_WhenNoComponents()
    {
        var vm = new DesignCanvasViewModel();
        var result = DesignCanvasHitTesting.HitTestComponent(new Point(50, 50), vm);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestComponent_FindsComponentAtPoint()
    {
        var vm = new DesignCanvasViewModel();
        var component = TestComponentFactory.CreateStraightWaveGuide();
        component.PhysicalX = 100;
        component.PhysicalY = 100;
        vm.AddComponent(component, "Template");

        // Center of component
        var result = DesignCanvasHitTesting.HitTestComponent(
            new Point(component.PhysicalX + component.WidthMicrometers / 2,
                      component.PhysicalY + component.HeightMicrometers / 2),
            vm);

        result.ShouldNotBeNull();
        result!.Component.ShouldBe(component);
    }

    [Fact]
    public void HitTestComponent_ReturnsNull_WhenPointOutsideComponent()
    {
        var vm = new DesignCanvasViewModel();
        var component = TestComponentFactory.CreateStraightWaveGuide();
        component.PhysicalX = 100;
        component.PhysicalY = 100;
        vm.AddComponent(component, "Template");

        // Far outside the component
        var result = DesignCanvasHitTesting.HitTestComponent(new Point(0, 0), vm);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestPin_ReturnsNull_WhenViewModelIsNull()
    {
        var result = DesignCanvasHitTesting.HitTestPin(new Point(0, 0), null);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestPin_ReturnsNull_WhenNoComponents()
    {
        var vm = new DesignCanvasViewModel();
        var result = DesignCanvasHitTesting.HitTestPin(new Point(0, 0), vm);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestPin_FindsPinWithinDefaultRadius_AtZoomOne()
    {
        var (vm, pin, pinX, pinY) = CreateComponentWithPin();

        var result = DesignCanvasHitTesting.HitTestPin(new Point(pinX + 12, pinY), vm, zoom: 1.0);

        result.ShouldBeSameAs(pin);
    }

    [Fact]
    public void HitTestPin_AtHighZoom_CapsHitRadiusToMatchTheCappedGlyphSize()
    {
        var (vm, _, pinX, pinY) = CreateComponentWithPin();

        // 12 µm away is within the uncapped 15 µm radius, but the screen-space cap at zoom 50
        // shrinks the effective radius to well under a micrometer — matching the capped glyph
        // PinRenderer draws, so the clickable area never outgrows what is visually shown.
        var result = DesignCanvasHitTesting.HitTestPin(new Point(pinX + 12, pinY), vm, zoom: 50.0);

        result.ShouldBeNull("the hit radius must cap in screen space just like the rendered glyph");
    }

    [Fact]
    public void HitTestPin_AtHighZoom_StillHitsAPointRightOnThePin()
    {
        var (vm, pin, pinX, pinY) = CreateComponentWithPin();

        var result = DesignCanvasHitTesting.HitTestPin(new Point(pinX, pinY), vm, zoom: 50.0);

        result.ShouldBeSameAs(pin, "clicking exactly on the pin must still hit it regardless of the cap");
    }

    private static (DesignCanvasViewModel Vm, CAP_Core.Components.Core.PhysicalPin Pin, double PinX, double PinY)
        CreateComponentWithPin()
    {
        var vm = new DesignCanvasViewModel();
        var terminal = UnitTests.Routing.CrossingInsertion.CrossingTestCircuit.CreateTerminal(
            "terminal", 100, 100, pinAngleDegrees: 0);
        vm.AddComponent(terminal.Component, "Template");
        var (pinX, pinY) = terminal.PhysicalPin.GetAbsolutePosition();
        return (vm, terminal.PhysicalPin, pinX, pinY);
    }

    [Fact]
    public void HitTestConnection_ReturnsNull_WhenViewModelIsNull()
    {
        var result = DesignCanvasHitTesting.HitTestConnection(new Point(0, 0), null);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestConnection_ReturnsNull_WhenNoConnections()
    {
        var vm = new DesignCanvasViewModel();
        var result = DesignCanvasHitTesting.HitTestConnection(new Point(50, 50), vm);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestConnection_PointOnLargeBendArc_HitsEvenFarFromTheChord()
    {
        // A 90° bend with 100 µm radius: the arc's midpoint sits ~41 µm off the
        // chord — way past the 10 px tolerance, so the chord approximation used
        // to miss hovers directly on the curve (hover lit up next to it instead).
        var vm = new DesignCanvasViewModel();
        var connection = new CAP_Core.Components.Connections.WaveguideConnection();
        var path = new CAP_Core.Routing.RoutedPath();
        path.Segments.Add(new CAP_Core.Routing.BendSegment(0, 0, 100, 0, 90));
        connection.RestoreCachedPath(path);
        var connVm = new WaveguideConnectionViewModel(connection);
        vm.Connections.Add(connVm);

        // Point on the arc at 45° (center (0,0), radius 100): (70.71, 70.71)
        // or (70.71, -70.71) depending on the sweep's sign convention — one of
        // them is exactly on the curve.
        var onArcA = new Point(70.71, 70.71);
        var onArcB = new Point(70.71, -70.71);
        var hit = DesignCanvasHitTesting.HitTestConnection(onArcA, vm)
            ?? DesignCanvasHitTesting.HitTestConnection(onArcB, vm);

        hit.ShouldBeSameAs(connVm, "a hover directly on a large bend's arc must hit the connection");

        // The chord's midpoint is ~41 µm away from the arc — hovering there must NOT hit.
        var chordMid = new Point(50, 50);
        var arcA = DesignCanvasHitTesting.HitTestConnection(onArcA, vm);
        var chordPoint = arcA != null ? onArcB : onArcA; // the OFF-curve diagonal
        var chordHit = DesignCanvasHitTesting.HitTestConnection(
            new Point((chordPoint.X + 0) / 2.0, (chordPoint.Y + 100) / 2.0), vm);
        chordHit.ShouldBeNull("the chord region is off the drawn curve and must stay unhit");
    }

    [Fact]
    public void HitTestCanvasFrozenPath_ReturnsNull_WhenViewModelIsNull()
    {
        var result = DesignCanvasHitTesting.HitTestCanvasFrozenPath(new Point(0, 0), null);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestCanvasFrozenPath_ReturnsNull_WhenNoPaths()
    {
        var vm = new DesignCanvasViewModel();
        var result = DesignCanvasHitTesting.HitTestCanvasFrozenPath(new Point(50, 50), vm);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestCanvasFrozenPath_HitsStraightSegmentWithinTolerance_MissesBeyond()
    {
        var (vm, pathVm) = CreateCanvasWithStraightFrozenPath();

        DesignCanvasHitTesting.HitTestCanvasFrozenPath(new Point(50, 105), vm)
            .ShouldBeSameAs(pathVm, "5 µm off the segment is inside the 10 µm tolerance");
        DesignCanvasHitTesting.HitTestCanvasFrozenPath(new Point(50, 125), vm)
            .ShouldBeNull("25 µm off the segment is outside the tolerance");
    }

    [Fact]
    public void HitTestCanvasFrozenPath_PointOnLargeBendArc_HitsEvenFarFromTheChord()
    {
        // Same arc-accuracy guarantee as HitTestConnection: hover, click and delete
        // share ONE hit test, so a frozen path's large bend must be hit on the curve.
        var vm = new DesignCanvasViewModel();
        var path = new CAP_Core.Routing.RoutedPath();
        path.Segments.Add(new CAP_Core.Routing.BendSegment(0, 0, 100, 0, 90));
        var pathVm = new CanvasFrozenPathViewModel(
            new CAP_Core.Components.Core.FrozenWaveguidePath { Path = path });
        vm.CanvasFrozenPaths.Add(pathVm);

        var onArcA = new Point(70.71, 70.71);
        var onArcB = new Point(70.71, -70.71);
        var hit = DesignCanvasHitTesting.HitTestCanvasFrozenPath(onArcA, vm)
            ?? DesignCanvasHitTesting.HitTestCanvasFrozenPath(onArcB, vm);

        hit.ShouldBeSameAs(pathVm, "a point directly on a large bend's arc must hit the frozen path");
    }

    private static (DesignCanvasViewModel Vm, CanvasFrozenPathViewModel PathVm)
        CreateCanvasWithStraightFrozenPath()
    {
        var vm = new DesignCanvasViewModel();
        var path = new CAP_Core.Routing.RoutedPath();
        path.Segments.Add(new CAP_Core.Routing.StraightSegment(0, 100, 100, 100, 0));
        var pathVm = new CanvasFrozenPathViewModel(
            new CAP_Core.Components.Core.FrozenWaveguidePath { Path = path });
        vm.CanvasFrozenPaths.Add(pathVm);
        return (vm, pathVm);
    }

    [Fact]
    public void HitTestGroupLabel_ReturnsNull_WhenViewModelIsNull()
    {
        var result = DesignCanvasHitTesting.HitTestGroupLabel(new Point(0, 0), null);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestGroupLabel_ReturnsNull_WhenNoGroups()
    {
        var vm = new DesignCanvasViewModel();
        var component = TestComponentFactory.CreateStraightWaveGuide();
        vm.AddComponent(component, "Template");

        // No groups, so no label to hit
        var result = DesignCanvasHitTesting.HitTestGroupLabel(new Point(50, 50), vm);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestGroupLockIcon_ReturnsNull_WhenNoGroups()
    {
        var vm = new DesignCanvasViewModel();
        var result = DesignCanvasHitTesting.HitTestGroupLockIcon(new Point(0, 0), vm);
        result.ShouldBeNull();
    }

    [Fact]
    public void HitTestGroupPin_ReturnsNullPin_WhenGroupIsNull()
    {
        var (pin, _) = DesignCanvasHitTesting.HitTestGroupPin(
            new Point(0, 0), null!, Enumerable.Empty<CAP_Core.Components.Connections.WaveguideConnection>());
        pin.ShouldBeNull();
    }
}
