using System.Numerics;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using MathNetVector = MathNet.Numerics.LinearAlgebra.Vector<System.Numerics.Complex>;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// Pins the steady-state behaviour of field-dependent (inner-loop) formula connections:
/// they are re-evaluated on every Neumann-series iteration because they depend on the
/// current field (e.g. logic gates switching on optical power), unlike parameter-only
/// (slider-driven) connections which are evaluated once before the iteration.
/// This only works when IsInnerLoopFunction is classified correctly (MathExpressionReader).
/// </summary>
public class InnerLoopFormulaEvaluationTests
{
    [Fact]
    public async Task InnerLoopConnection_IsReevaluatedDuringIteration()
    {
        var pin = new Pin("p", 0, MatterType.Light, RectSide.Left);
        var sMatrix = new SMatrix(new List<Guid> { pin.IDInFlow, pin.IDOutFlow }, new());

        int evaluationCount = 0;
        var innerLoopFn = new ConnectionFunction(
            weights => { evaluationCount++; return Complex.Zero; },
            "PIN1 * 0",
            new List<Guid> { pin.IDInFlow },
            IsInnerLoopFunction: true);
        sMatrix.NonLinearConnections.Add((pin.IDInFlow, pin.IDOutFlow), innerLoopFn);

        var input = MathNetVector.Build.Dense(2);
        input[sMatrix.PinReference[pin.IDInFlow]] = Complex.One;

        await sMatrix.CalcFieldAtPinsAfterStepsAsync(input, maxIterations: 5, new CancellationTokenSource());

        // Once before the iteration (all functions are evaluated there) plus at least
        // once inside it (inner-loop only). With the old classification bug the flag was
        // always false, so the in-loop evaluation never happened and the count stayed 1.
        evaluationCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ParameterOnlyConnection_IsEvaluatedOnceBeforeIteration()
    {
        var pin = new Pin("p", 0, MatterType.Light, RectSide.Left);
        var sliderId = Guid.NewGuid();
        var sMatrix = new SMatrix(
            new List<Guid> { pin.IDInFlow, pin.IDOutFlow },
            new List<(Guid sliderID, double value)> { (sliderId, 0.5) });

        int evaluationCount = 0;
        var parameterOnlyFn = new ConnectionFunction(
            weights => { evaluationCount++; return Complex.Zero; },
            "SLIDER1 * 0",
            new List<Guid> { sliderId },
            IsInnerLoopFunction: false);
        sMatrix.NonLinearConnections.Add((pin.IDInFlow, pin.IDOutFlow), parameterOnlyFn);

        var input = MathNetVector.Build.Dense(2);
        input[sMatrix.PinReference[pin.IDInFlow]] = Complex.One;

        await sMatrix.CalcFieldAtPinsAfterStepsAsync(input, maxIterations: 5, new CancellationTokenSource());

        // Evaluated exactly once before the iteration; skipped inside the loop.
        evaluationCount.ShouldBe(1);
    }
}
