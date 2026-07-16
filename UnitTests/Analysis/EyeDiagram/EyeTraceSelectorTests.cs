using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using Shouldly;

namespace UnitTests.Analysis.EyeDiagram;

/// <summary>
/// Tests for <see cref="EyeTraceSelector"/> (#690): the eye analysis must prefer
/// traces at couplers whose laser is switched off (the design's true outputs),
/// warn when every laser is still on, and error when the designated outputs
/// receive no light.
/// </summary>
public class EyeTraceSelectorTests
{
    private static TimeDomainResult BuildResult(params (Guid PinId, double[] Trace)[] traces)
    {
        var dict = traces.ToDictionary(t => t.PinId, t => t.Trace);
        var timeAxis = new double[traces.Length == 0 ? 0 : traces[0].Trace.Length];
        return new TimeDomainResult(timeAxis, dict);
    }

    [Fact]
    public void Select_PrefersOffCouplerTrace_EvenWhenWeaker()
    {
        var outputPin = Guid.NewGuid();
        var otherPin = Guid.NewGuid();
        var result = BuildResult(
            (outputPin, new[] { 0.1, 0.2 }),
            (otherPin, new[] { 5.0, 9.0 }));

        var selection = EyeTraceSelector.Select(result, new[] { outputPin });

        selection.Trace.ShouldBe(result.PinTraces[outputPin]);
        selection.Warning.ShouldBeNull();
        selection.Error.ShouldBeNull();
    }

    [Fact]
    public void Select_PicksStrongestAmongMultipleOffCouplers()
    {
        var weakOutput = Guid.NewGuid();
        var strongOutput = Guid.NewGuid();
        var result = BuildResult(
            (weakOutput, new[] { 0.1, 0.2 }),
            (strongOutput, new[] { 0.3, 0.8 }));

        var selection = EyeTraceSelector.Select(result, new[] { weakOutput, strongOutput });

        selection.Trace.ShouldBe(result.PinTraces[strongOutput]);
        selection.Error.ShouldBeNull();
    }

    [Fact]
    public void Select_FallsBackToStrongestWithWarning_WhenAllLasersOn()
    {
        var weakPin = Guid.NewGuid();
        var strongPin = Guid.NewGuid();
        var result = BuildResult(
            (weakPin, new[] { 0.1, 0.2 }),
            (strongPin, new[] { 5.0, 9.0 }));

        var selection = EyeTraceSelector.Select(result, Array.Empty<Guid>());

        selection.Trace.ShouldBe(result.PinTraces[strongPin]);
        selection.Warning.ShouldBe(EyeTraceSelector.AllLasersOnWarning);
        selection.Error.ShouldBeNull();
    }

    [Fact]
    public void Select_ReturnsError_WhenNoTraceReachesOffCoupler()
    {
        var outputPin = Guid.NewGuid();
        var unrelatedPin = Guid.NewGuid();
        var result = BuildResult((unrelatedPin, new[] { 1.0, 2.0 }));

        var selection = EyeTraceSelector.Select(result, new[] { outputPin });

        selection.Trace.ShouldBeNull();
        selection.Error.ShouldBe(EyeTraceSelector.NoSignalAtOutputError);
        selection.Warning.ShouldBeNull();
    }

    [Fact]
    public void Select_HandlesEmptyTraceArrays()
    {
        var outputPin = Guid.NewGuid();
        var emptyPin = Guid.NewGuid();
        var result = BuildResult(
            (outputPin, new[] { 0.5 }),
            (emptyPin, Array.Empty<double>()));

        var selection = EyeTraceSelector.Select(result, new[] { outputPin, emptyPin });

        selection.Trace.ShouldBe(result.PinTraces[outputPin]);
        selection.Error.ShouldBeNull();
    }
}
