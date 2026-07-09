using CAP_Core.Components.Core;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PolarizationDomain;

/// <summary>
/// Tests that the pin data model carries polarization: TE default for
/// backward compatibility, clone preservation, and physical-pin passthrough.
/// </summary>
public class PinPolarizationTests
{
    [Fact]
    public void Pin_DefaultsToTePolarization()
    {
        var pin = new Pin("a0", 0, MatterType.Light, RectSide.Left);

        pin.Polarization.ShouldBe(PolarizationKind.TE);
    }

    [Fact]
    public void Pin_Clone_PreservesPolarization()
    {
        var pin = new Pin("a0", 0, MatterType.Light, RectSide.Left)
        {
            Polarization = PolarizationKind.TM
        };

        var clone = (Pin)pin.Clone();

        clone.Polarization.ShouldBe(PolarizationKind.TM);
    }

    [Fact]
    public void PhysicalPin_DerivesPolarizationFromLogicalPin()
    {
        var logicalPin = new Pin("a0", 0, MatterType.Light, RectSide.Left)
        {
            Polarization = PolarizationKind.Both
        };
        var physicalPin = new PhysicalPin { Name = "a0", LogicalPin = logicalPin };

        physicalPin.Polarization.ShouldBe(PolarizationKind.Both);
    }

    [Fact]
    public void PhysicalPin_WithoutLogicalPin_DefaultsToTe()
    {
        var physicalPin = new PhysicalPin { Name = "a0" };

        physicalPin.Polarization.ShouldBe(PolarizationKind.TE);
    }
}
