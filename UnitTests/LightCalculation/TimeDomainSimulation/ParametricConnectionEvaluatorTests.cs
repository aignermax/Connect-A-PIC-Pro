using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

public class ParametricConnectionEvaluatorTests
{
    private static readonly Guid InputPinId = Guid.NewGuid();
    private static readonly Guid OutputPinId = Guid.NewGuid();
    private static readonly Guid SliderId = Guid.NewGuid();
    private const double SliderValue = 65.0;

    private static SMatrix CreateMatrixWithSlider() =>
        new(new List<Guid> { InputPinId, OutputPinId }, new() { (SliderId, SliderValue) });

    private static ConnectionFunction SliderOnlyFunction() => new(
        parameters => new Complex((double)parameters[0] / 100.0, 0),
        "SR/100",
        new List<Guid> { SliderId },
        IsInnerLoopFunction: false);

    [Fact]
    public void IsTrulyNonLinear_SliderOnlyFormula_ReturnsFalse()
    {
        var matrix = CreateMatrixWithSlider();

        ParametricConnectionEvaluator.IsTrulyNonLinear(matrix, SliderOnlyFunction())
            .ShouldBeFalse("a slider-bound formula is a constant at simulation time");
    }

    [Fact]
    public void IsTrulyNonLinear_PinReferencingFormula_ReturnsTrue()
    {
        var matrix = CreateMatrixWithSlider();
        var pinDependent = new ConnectionFunction(
            _ => Complex.One, "abs(P1)", new List<Guid> { InputPinId },
            IsInnerLoopFunction: false);

        ParametricConnectionEvaluator.IsTrulyNonLinear(matrix, pinDependent).ShouldBeTrue();
    }

    [Fact]
    public void IsTrulyNonLinear_InnerLoopFlag_ReturnsTrue()
    {
        var matrix = CreateMatrixWithSlider();
        var innerLoop = new ConnectionFunction(
            _ => Complex.One, "1", new List<Guid>(), IsInnerLoopFunction: true);

        ParametricConnectionEvaluator.IsTrulyNonLinear(matrix, innerLoop).ShouldBeTrue();
    }

    [Fact]
    public void CountTrulyNonLinear_MixedConnections_CountsOnlyPinDependentOnes()
    {
        var matrix = CreateMatrixWithSlider();
        matrix.NonLinearConnections.Add((InputPinId, OutputPinId), SliderOnlyFunction());
        matrix.NonLinearConnections.Add((OutputPinId, InputPinId), new ConnectionFunction(
            _ => Complex.One, "abs(P1)", new List<Guid> { InputPinId },
            IsInnerLoopFunction: false));

        ParametricConnectionEvaluator.CountTrulyNonLinear(matrix).ShouldBe(1);
    }

    [Fact]
    public void EvaluateParametricConnections_WritesSliderEvaluatedWeightIntoMatrix()
    {
        var matrix = CreateMatrixWithSlider();
        matrix.NonLinearConnections.Add((InputPinId, OutputPinId), SliderOnlyFunction());

        ParametricConnectionEvaluator.EvaluateParametricConnections(matrix);

        var values = matrix.GetNonNullValues();
        values[(InputPinId, OutputPinId)].ShouldBe(new Complex(SliderValue / 100.0, 0));
    }

    [Fact]
    public void EvaluateParametricConnections_LeavesTrulyNonLinearUntouched()
    {
        var matrix = CreateMatrixWithSlider();
        matrix.NonLinearConnections.Add((InputPinId, OutputPinId), new ConnectionFunction(
            _ => Complex.One, "abs(P1)", new List<Guid> { InputPinId },
            IsInnerLoopFunction: false));

        ParametricConnectionEvaluator.EvaluateParametricConnections(matrix);

        matrix.GetNonNullValues().ShouldBeEmpty(
            "pin-dependent formulas must not be baked in as constants");
    }
}
