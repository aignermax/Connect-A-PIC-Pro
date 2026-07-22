using CAP.Avalonia.Controls.Rendering;
using CAP_Core.Components.Core;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Tests for <see cref="PinConnectionAffordance"/>: the drag-preview dimming check (issue #724
/// point 4). Before this fix, <see cref="CAP.Avalonia.Controls.Rendering.PinRenderer"/> only
/// dimmed on a polarization mismatch — a domain mismatch (optical vs. electrical) showed no
/// visual affordance during the drag, only a rejection once the user released.
/// </summary>
public class PinConnectionAffordanceTests
{
    [Fact]
    public void IsIncompatibleTarget_SameDomainSamePolarization_ReturnsFalse()
    {
        var start = CreatePin(MatterType.Light, PolarizationKind.TE);
        var candidate = CreatePin(MatterType.Light, PolarizationKind.TE);

        PinConnectionAffordance.IsIncompatibleTarget(start, candidate).ShouldBeFalse();
    }

    [Fact]
    public void IsIncompatibleTarget_OpticalToElectrical_ReturnsTrue()
    {
        // The gap this issue closes: a domain mismatch must dim the target too, not just a
        // polarization mismatch.
        var opticalStart = CreatePin(MatterType.Light, PolarizationKind.TE);
        var electricalCandidate = CreatePin(MatterType.Electricity, PolarizationKind.TE);

        PinConnectionAffordance.IsIncompatibleTarget(opticalStart, electricalCandidate).ShouldBeTrue();
    }

    [Fact]
    public void IsIncompatibleTarget_TeToTm_ReturnsTrue()
    {
        var teStart = CreatePin(MatterType.Light, PolarizationKind.TE);
        var tmCandidate = CreatePin(MatterType.Light, PolarizationKind.TM);

        PinConnectionAffordance.IsIncompatibleTarget(teStart, tmCandidate).ShouldBeTrue();
    }

    [Fact]
    public void IsIncompatibleTarget_BothPolarizationAcceptsEitherOpticalPin()
    {
        var start = CreatePin(MatterType.Light, PolarizationKind.Both);
        var teCandidate = CreatePin(MatterType.Light, PolarizationKind.TE);
        var tmCandidate = CreatePin(MatterType.Light, PolarizationKind.TM);

        PinConnectionAffordance.IsIncompatibleTarget(start, teCandidate).ShouldBeFalse();
        PinConnectionAffordance.IsIncompatibleTarget(start, tmCandidate).ShouldBeFalse();
    }

    private static PhysicalPin CreatePin(MatterType matterType, PolarizationKind polarization) => new()
    {
        Name = "p0",
        LogicalPin = new Pin("p0", 0, matterType, RectSide.Right) { Polarization = polarization },
    };
}
