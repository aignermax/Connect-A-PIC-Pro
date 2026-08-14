using CAP_Core.Analysis;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Unit tests for <see cref="DesignValidator.ValidateUnconnectedPins"/>.
/// </summary>
public class DesignValidatorUnconnectedPinTests
{
    private readonly DesignValidator _validator = new();

    [Fact]
    public void ValidateUnconnectedPins_FullyConnected_ReturnsNoIssues()
    {
        var (comp1, comp2) = CreateConnectedPair();

        var result = _validator.ValidateUnconnectedPins(
            new[] { comp1, comp2 },
            new[] { new WaveguideConnection { StartPin = comp1.PhysicalPins[0], EndPin = comp2.PhysicalPins[0] } });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateUnconnectedPins_DanglingPin_ReturnsWarning()
    {
        var comp1 = CreateComponentWithPins("comp1", 0, 0, new[] { "out", "unused" });
        var comp2 = CreateComponentWithPins("comp2", 100, 0, new[] { "in" });
        var connection = new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };

        var result = _validator.ValidateUnconnectedPins(
            new[] { comp1, comp2 },
            new[] { connection });

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.UnconnectedPin);
        result[0].Description.ShouldContain("comp1");
        result[0].Description.ShouldContain("unused");
    }

    [Fact]
    public void ValidateUnconnectedPins_ExternalPortExcluded_ReturnsNoIssue()
    {
        var comp1 = CreateComponentWithPins("comp1", 0, 0, new[] { "out" });
        var danglingPin = comp1.PhysicalPins[0];

        var result = _validator.ValidateUnconnectedPins(
            new[] { comp1 },
            Array.Empty<WaveguideConnection>(),
            new[] { danglingPin });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateUnconnectedPins_ElectricalPinIgnored()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        var logicalPin = new Pin("elec0", 0, MatterType.Electricity, RectSide.Left);
        comp.Parts[0, 0].Pins.Add(logicalPin);
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "elec0",
            ParentComponent = comp,
            LogicalPin = logicalPin
        });

        var result = _validator.ValidateUnconnectedPins(
            new[] { comp },
            Array.Empty<WaveguideConnection>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateUnconnectedPins_Position_IsPinAbsolutePosition()
    {
        var comp = CreateComponentWithPins("comp", 100, 200, new[] { "dangling" });

        var result = _validator.ValidateUnconnectedPins(
            new[] { comp },
            Array.Empty<WaveguideConnection>());

        result[0].X.ShouldBe(100, 0.1);
        result[0].Y.ShouldBe(200, 0.1);
    }

    [Fact]
    public void ValidateUnconnectedPins_NullArguments_ThrowArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateUnconnectedPins(null!, Array.Empty<WaveguideConnection>()));
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateUnconnectedPins(Array.Empty<Component>(), null!));
    }

    [Fact]
    public void Validate_WithComponents_IncludesUnconnectedPinCheck()
    {
        var comp = CreateComponentWithPins("comp", 0, 0, new[] { "dangling" });

        var result = _validator.Validate(
            Array.Empty<WaveguideConnection>(),
            new[] { comp });

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.UnconnectedPin);
    }

    [Fact]
    public void Validate_WithComponentsAndGroups_IncludesUnconnectedPinCheck()
    {
        var comp = CreateComponentWithPins("comp", 0, 0, new[] { "dangling" });
        var group = new ComponentGroup("test");

        var result = _validator.Validate(
            Array.Empty<WaveguideConnection>(),
            new[] { group },
            new[] { comp });

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.UnconnectedPin);
    }

    private static (Component Comp1, Component Comp2) CreateConnectedPair()
    {
        var comp1 = CreateComponentWithPins("comp1", 0, 0, new[] { "out" });
        var comp2 = CreateComponentWithPins("comp2", 100, 0, new[] { "in" });
        return (comp1, comp2);
    }

    private static Component CreateComponentWithPins(
        string identifier,
        double x,
        double y,
        string[] pinNames)
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();
        component.Identifier = identifier;
        component.PhysicalX = x;
        component.PhysicalY = y;

        foreach (var name in pinNames)
        {
            component.PhysicalPins.Add(new PhysicalPin
            {
                Name = name,
                ParentComponent = component,
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 0
            });
        }

        return component;
    }
}
