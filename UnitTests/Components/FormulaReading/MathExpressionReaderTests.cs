using CAP_Core.Components.FormulaReading;
using CAP_Core.Grid.FormulaReading;
using Shouldly;
using Xunit;

namespace UnitTests.Components.FormulaReading;

public class MathExpressionReaderTests
{
    private static ConnectionFunction Convert(
        string expression,
        Dictionary<string, Guid>? pinMap = null,
        Dictionary<string, Guid>? sliderMap = null)
    {
        return MathExpressionReader.ConvertToDelegate(
            expression,
            pinMap ?? new Dictionary<string, Guid>(),
            sliderMap ?? new Dictionary<string, Guid>());
    }

    [Fact]
    public void ConvertToDelegate_SliderOnlyFormula_IsNotInnerLoop()
    {
        var sliderId = Guid.NewGuid();

        var fn = Convert("SLIDER1 * 0.5", sliderMap: new() { ["SLIDER1"] = sliderId });

        fn.IsInnerLoopFunction.ShouldBeFalse();
        fn.UsedParameterGuids.ShouldBe(new[] { sliderId });
    }

    [Fact]
    public void ConvertToDelegate_PinDependentFormula_IsInnerLoop()
    {
        var pinId = Guid.NewGuid();

        var fn = Convert("PIN1 * 2", pinMap: new() { ["PIN1"] = pinId });

        fn.IsInnerLoopFunction.ShouldBeTrue();
        fn.UsedParameterGuids.ShouldBe(new[] { pinId });
    }

    [Fact]
    public void ConvertToDelegate_MixedPinAndSliderFormula_IsInnerLoop()
    {
        var pinId = Guid.NewGuid();
        var sliderId = Guid.NewGuid();

        var fn = Convert("PIN1 * SLIDER1",
            pinMap: new() { ["PIN1"] = pinId },
            sliderMap: new() { ["SLIDER1"] = sliderId });

        // any pin involvement makes the connection field-dependent, no matter
        // whether a slider appears before or after the pin in the expression
        fn.IsInnerLoopFunction.ShouldBeTrue();
        fn.UsedParameterGuids.ShouldContain(pinId);
        fn.UsedParameterGuids.ShouldContain(sliderId);
    }

    [Fact]
    public void ConvertToDelegate_ConstantExpression_IsNotInnerLoop()
    {
        var fn = Convert("0.707");

        fn.IsInnerLoopFunction.ShouldBeFalse();
        fn.UsedParameterGuids.ShouldBeEmpty();
    }
}
