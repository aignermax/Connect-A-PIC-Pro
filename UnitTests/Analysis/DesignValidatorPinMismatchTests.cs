using CAP_Core.Analysis;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Unit tests for <see cref="DesignValidator"/> pin width/layer mismatch detection.
/// </summary>
public class DesignValidatorPinMismatchTests
{
    private readonly DesignValidator _validator = new();

    [Fact]
    public void Validate_MatchedPinWidthAndLayer_ReturnsNoIssue()
    {
        var (startPin, endPin) = CreatePinPair(widthMicrometers: 0.5, layer: 10);
        var connection = new WaveguideConnection { StartPin = startPin, EndPin = endPin };

        var result = _validator.Validate(new[] { connection });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WidthMismatch_ReturnsPinMismatchError()
    {
        var comp1 = CreateComponentWithPin("out", 0, 0, widthMicrometers: 0.5, layer: 10);
        var comp2 = CreateComponentWithPin("in", 100, 0, widthMicrometers: 1.2, layer: 10);
        var connection = new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };

        var result = _validator.Validate(new[] { connection });

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.PinMismatch);
        result[0].Description.ShouldContain("0.5");
        result[0].Description.ShouldContain("1.2");
        result[0].Description.ShouldContain(comp1.Identifier);
        result[0].Description.ShouldContain(comp2.Identifier);
    }

    [Fact]
    public void Validate_LayerMismatch_ReturnsPinMismatchError()
    {
        var comp1 = CreateComponentWithPin("out", 0, 0, widthMicrometers: 0.5, layer: 10);
        var comp2 = CreateComponentWithPin("in", 100, 0, widthMicrometers: 0.5, layer: 20);
        var connection = new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };

        var result = _validator.Validate(new[] { connection });

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.PinMismatch);
        result[0].Description.ShouldContain("layer 10");
        result[0].Description.ShouldContain("layer 20");
    }

    [Fact]
    public void Validate_WidthAndLayerMismatch_ReturnsTwoIssues()
    {
        var comp1 = CreateComponentWithPin("out", 0, 0, widthMicrometers: 0.5, layer: 10);
        var comp2 = CreateComponentWithPin("in", 100, 0, widthMicrometers: 1.2, layer: 20);
        var connection = new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };

        var result = _validator.Validate(new[] { connection });

        result.Count.ShouldBe(2);
        result.ShouldContain(i => i.Description.Contains("width"));
        result.ShouldContain(i => i.Description.Contains("layer"));
    }

    [Fact]
    public void Validate_NoWidthOrLayerData_ReturnsNoIssue()
    {
        var comp1 = TestComponentFactory.CreateStraightWaveGuide();
        var comp2 = TestComponentFactory.CreateStraightWaveGuide();
        comp1.PhysicalPins.Add(new PhysicalPin { Name = "out", ParentComponent = comp1 });
        comp2.PhysicalPins.Add(new PhysicalPin { Name = "in", ParentComponent = comp2 });
        var connection = new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };

        var result = _validator.Validate(new[] { connection });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_PinMismatchPosition_IsMidpointOfPins()
    {
        var comp1 = CreateComponentWithPin("out", 100, 200, widthMicrometers: 0.5, layer: 10);
        var comp2 = CreateComponentWithPin("in", 300, 400, widthMicrometers: 1.2, layer: 10);
        var connection = new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };

        var result = _validator.Validate(new[] { connection });

        result[0].X.ShouldBe(200, 0.1);
        result[0].Y.ShouldBe(300, 0.1);
    }

    [Fact]
    public void Validate_PinMismatchWithGeometryIssue_ReturnsBothIssues()
    {
        var comp1 = CreateComponentWithPin("out", 0, 0, widthMicrometers: 0.5, layer: 10);
        var comp2 = CreateComponentWithPin("in", 100, 0, widthMicrometers: 1.2, layer: 10);
        var connection = new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        path.IsInvalidGeometry = true;
        connection.RestoreCachedPath(path);

        var result = _validator.Validate(new[] { connection });

        result.Count.ShouldBe(2);
        result.ShouldContain(i => i.Type == DesignIssueType.InvalidGeometry);
        result.ShouldContain(i => i.Type == DesignIssueType.PinMismatch);
    }

    private static (PhysicalPin StartPin, PhysicalPin EndPin) CreatePinPair(
        double widthMicrometers, int layer)
    {
        var comp1 = CreateComponentWithPin("out", 0, 0, widthMicrometers, layer);
        var comp2 = CreateComponentWithPin("in", 100, 0, widthMicrometers, layer);
        return (comp1.PhysicalPins[0], comp2.PhysicalPins[0]);
    }

    private static Component CreateComponentWithPin(
        string pinName,
        double x,
        double y,
        double widthMicrometers,
        int? layer)
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();
        component.Identifier = $"{component.Identifier}_{pinName}_{x}";
        component.PhysicalX = x;
        component.PhysicalY = y;
        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = pinName,
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            WaveguideWidthMicrometers = widthMicrometers,
            Layer = layer
        });
        return component;
    }
}
