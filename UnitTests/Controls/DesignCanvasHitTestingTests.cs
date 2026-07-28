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
