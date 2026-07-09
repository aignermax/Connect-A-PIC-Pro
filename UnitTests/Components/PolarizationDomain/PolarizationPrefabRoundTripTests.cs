using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PolarizationDomain;

/// <summary>
/// Tests that prefab (group template) serialization round-trips per-pin
/// polarization, and that old prefab JSON without the field loads as TE
/// (issue #534).
/// </summary>
public class PolarizationPrefabRoundTripTests
{
    private static Component CreateComponentWithPolarizedPin(PolarizationKind polarization)
    {
        var logicalPin = new Pin("a0", 0, MatterType.Light, RectSide.Left)
        {
            Polarization = polarization
        };
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin> { logicalPin });

        var pinIds = new List<Guid> { logicalPin.IDInFlow, logicalPin.IDOutFlow };
        var sMatrix = new SMatrix(pinIds, new());
        sMatrix.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            { (logicalPin.IDInFlow, logicalPin.IDOutFlow), Complex.One }
        });

        var physicalPin = new PhysicalPin
        {
            Name = "a0",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 180,
            LogicalPin = logicalPin
        };

        return new Component(
            new Dictionary<int, SMatrix> { { 1550, sMatrix } },
            new List<Slider>(), "test_comp", "", parts, 0, "TestComp",
            DiscreteRotation.R0, new List<PhysicalPin> { physicalPin });
    }

    [Theory]
    [InlineData(PolarizationKind.TE)]
    [InlineData(PolarizationKind.TM)]
    [InlineData(PolarizationKind.Both)]
    public void SerializeDeserialize_PreservesPinPolarization(PolarizationKind polarization)
    {
        var group = new ComponentGroup("PolarizationGroup");
        group.AddChild(CreateComponentWithPolarizedPin(polarization));

        var json = GroupTemplateSerializer.Serialize(group);
        var restored = GroupTemplateSerializer.Deserialize(json);

        restored.ShouldNotBeNull();
        var restoredPin = restored.ChildComponents[0].PhysicalPins.Single();
        restoredPin.Polarization.ShouldBe(polarization);
        restoredPin.LogicalPin.Polarization.ShouldBe(polarization);
    }

    [Fact]
    public void Deserialize_OldJsonWithoutPolarizationField_DefaultsToTe()
    {
        var group = new ComponentGroup("LegacyGroup");
        group.AddChild(CreateComponentWithPolarizedPin(PolarizationKind.TM));

        // Simulate an old prefab file created before the polarization field existed.
        var json = System.Text.RegularExpressions.Regex.Replace(
            GroupTemplateSerializer.Serialize(group),
            @",?\s*""Polarization"":\s*""TM""", "");
        json.ShouldNotContain("\"Polarization\"");

        var restored = GroupTemplateSerializer.Deserialize(json);

        restored.ShouldNotBeNull();
        restored.ChildComponents[0].PhysicalPins.Single()
            .Polarization.ShouldBe(PolarizationKind.TE);
    }
}
