using System.Collections.ObjectModel;
using System.Numerics;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.Services;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PolarizationDomain;

/// <summary>
/// Tests that <see cref="PinHighlightService"/> never highlights a
/// polarization-incompatible pin while dragging a connection (issue #534).
/// </summary>
public class PolarizationPinHighlightTests
{
    private static (ComponentViewModel vm, PhysicalPin pin) CreateComponentAt(
        string name, double x, PolarizationKind polarization)
    {
        var logicalPin = new Pin("a0", 0, MatterType.Light, RectSide.Left)
        {
            Polarization = polarization
        };
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin> { logicalPin });

        var physicalPin = new PhysicalPin
        {
            Name = "a0",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = 180,
            LogicalPin = logicalPin
        };

        var component = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(), "test", "", parts, 0, name,
            DiscreteRotation.R0, new List<PhysicalPin> { physicalPin });
        component.PhysicalX = x;
        component.PhysicalY = 0;

        return (new ComponentViewModel(component), physicalPin);
    }

    [Fact]
    public void UpdatePinHighlight_DuringDragFromTePin_SkipsTmPins()
    {
        var (sourceVm, tePin) = CreateComponentAt("Source_TE", 0, PolarizationKind.TE);
        var (targetVm, tmPin) = CreateComponentAt("Target_TM", 100, PolarizationKind.TM);

        var allPins = new ObservableCollection<PinViewModel>
        {
            new(tePin, sourceVm),
            new(tmPin, targetVm)
        };
        var service = new PinHighlightService(allPins, _ => null);

        // Hover exactly over the TM pin while dragging from the TE pin.
        var highlighted = service.UpdatePinHighlight(100, 0, excludePin: tePin);

        highlighted.ShouldBeNull("TM pin must not be a highlightable target for a TE drag");
    }

    [Fact]
    public void UpdatePinHighlight_DuringDragFromTePin_AllowsBothPins()
    {
        var (sourceVm, tePin) = CreateComponentAt("Source_TE", 0, PolarizationKind.TE);
        var (targetVm, bothPin) = CreateComponentAt("Target_Both", 100, PolarizationKind.Both);

        var allPins = new ObservableCollection<PinViewModel>
        {
            new(tePin, sourceVm),
            new(bothPin, targetVm)
        };
        var service = new PinHighlightService(allPins, _ => null);

        var highlighted = service.UpdatePinHighlight(100, 0, excludePin: tePin);

        highlighted.ShouldNotBeNull("Both-polarization pin must remain a valid target");
        highlighted.Pin.ShouldBe(bothPin);
    }
}
