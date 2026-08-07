using System.Collections.ObjectModel;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.Services;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Helpers;

namespace UnitTests.ViewModels.Services;

/// <summary>
/// Unit tests for PinHighlightService - pin highlighting and nearest-pin search.
/// </summary>
public class PinHighlightServiceTests
{
    private readonly ObservableCollection<PinViewModel> _allPins = new();
    private readonly PinHighlightService _service;

    public PinHighlightServiceTests()
    {
        _service = new PinHighlightService(_allPins, _ => null);
    }

    [Fact]
    public void UpdatePinHighlight_NoPins_ReturnsNull()
    {
        var result = _service.UpdatePinHighlight(100, 100);
        result.ShouldBeNull();
        _service.HighlightedPin.ShouldBeNull();
    }

    [Fact]
    public void UpdatePinHighlight_PinWithinDistance_ReturnsAndHighlights()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        var pinPos = comp.PhysicalPins[0].GetAbsolutePosition();
        var result = _service.UpdatePinHighlight(pinPos.Item1, pinPos.Item2);

        result.ShouldNotBeNull();
        result.IsHighlighted.ShouldBeTrue();
        _service.HighlightedPin.ShouldBe(result);
    }

    [Fact]
    public void UpdatePinHighlight_PinBeyondDistance_ReturnsNull()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        // Very far from any pin
        var result = _service.UpdatePinHighlight(5000, 5000);
        result.ShouldBeNull();
    }

    [Fact]
    public void UpdatePinHighlight_ClearsPreviousHighlight()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        var pinPos = comp.PhysicalPins[0].GetAbsolutePosition();
        var first = _service.UpdatePinHighlight(pinPos.Item1, pinPos.Item2);
        first.ShouldNotBeNull();

        // Move far away - should clear previous highlight
        _service.UpdatePinHighlight(5000, 5000);
        first!.IsHighlighted.ShouldBeFalse();
        _service.HighlightedPin.ShouldBeNull();
    }

    [Fact]
    public void ClearPinHighlight_ClearsHighlightState()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        var pinPos = comp.PhysicalPins[0].GetAbsolutePosition();
        _service.UpdatePinHighlight(pinPos.Item1, pinPos.Item2);

        _service.ClearPinHighlight();

        _service.HighlightedPin.ShouldBeNull();
    }

    [Fact]
    public void GetPinAt_NearPin_ReturnsPin()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        var pinPos = comp.PhysicalPins[0].GetAbsolutePosition();
        var result = _service.GetPinAt(pinPos.Item1 + 1, pinPos.Item2 + 1);

        result.ShouldNotBeNull();
        result.ShouldBe(comp.PhysicalPins[0]);
    }

    [Fact]
    public void GetPinAt_FarFromPin_ReturnsNull()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        var result = _service.GetPinAt(5000, 5000);
        result.ShouldBeNull();
    }

    [Fact]
    public void UpdatePinHighlight_ExcludesSpecifiedPin()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        var excludePin = comp.PhysicalPins[0];
        var pinPos = excludePin.GetAbsolutePosition();

        // Excluding the pin itself returns null: the waveguide's other pin is
        // 250 µm away, far beyond the highlight distance (same-component pins
        // are NO longer excluded — see the in-range test below).
        var result = _service.UpdatePinHighlight(pinPos.Item1, pinPos.Item2, excludePin);
        result.ShouldBeNull();
    }

    [Fact]
    public void UpdatePinHighlight_SameComponentPinInRange_IsHighlighted()
    {
        // Dragging from one pin of a component must offer its OTHER pins as
        // targets (feedback loops, ring self-coupling, black-box imports).
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        var startPin = comp.PhysicalPins[0]; // "in", west side
        var otherPin = comp.PhysicalPins[1]; // "out", east side
        var (otherX, otherY) = otherPin.GetAbsolutePosition();

        var result = _service.UpdatePinHighlight(otherX, otherY, startPin);

        result.ShouldNotBeNull("a different pin of the same component is a valid connection target");
        result.Pin.ShouldBe(otherPin);
        result.IsHighlighted.ShouldBeTrue();
    }

    [Fact]
    public void PinHighlightDistance_IsConfigurable()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var vm = new ComponentViewModel(comp);

        foreach (var pin in comp.PhysicalPins)
            _allPins.Add(new PinViewModel(pin, vm));

        var pinPos = comp.PhysicalPins[0].GetAbsolutePosition();

        // Default distance (15µm) - should find pin at close range
        _service.PinHighlightDistance = 15.0;
        var result1 = _service.UpdatePinHighlight(pinPos.Item1 + 10, pinPos.Item2);
        result1.ShouldNotBeNull();

        // Very small distance - same offset should not find pin
        _service.PinHighlightDistance = 1.0;
        var result2 = _service.UpdatePinHighlight(pinPos.Item1 + 10, pinPos.Item2);
        result2.ShouldBeNull();
    }

    [Fact]
    public void HighlightChanged_EventFires_OnHighlightUpdate()
    {
        int eventCount = 0;
        _service.HighlightChanged += () => eventCount++;

        _service.UpdatePinHighlight(100, 100);
        eventCount.ShouldBe(1);

        _service.ClearPinHighlight();
        // ClearPinHighlight only fires if there was a highlighted pin
        eventCount.ShouldBe(1); // No pin was highlighted, so no event
    }
}
