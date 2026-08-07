using System.Numerics;
using CAP_Core.Analysis.CircuitOptimization;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using Moq;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Analysis.CircuitOptimization;

public class CircuitOptimizerTests
{
    private const int FixedSeed = 42;
    private static readonly Guid OutputPin = Guid.NewGuid();

    /// <summary>
    /// Fake MZI transfer: output power = sin²(π·k), maximal at coupling k = 0.5.
    /// </summary>
    private static Mock<ILightCalculator> CreateMziCalculator(Component component, Action? onEvaluate = null)
    {
        var mock = new Mock<ILightCalculator>();
        mock.Setup(c => c.CalculateFieldPropagationAsync(
                It.IsAny<CancellationTokenSource>(), It.IsAny<int>()))
            .Returns(() =>
            {
                onEvaluate?.Invoke();
                double k = component.GetSlider(0)!.Value;
                double amplitude = Math.Sin(Math.PI * k);
                return Task.FromResult(new Dictionary<Guid, Complex>
                {
                    { OutputPin, new Complex(amplitude, 0) }
                });
            });
        return mock;
    }

    /// <summary>
    /// The Component constructor resets sliders to their midpoint, so the start
    /// value must be set explicitly after construction.
    /// </summary>
    private static Component CreateComponentAt(double startValue)
    {
        var component = TestComponentHelper.CreateComponentWithSlider(0, 1, startValue);
        component.GetSlider(0)!.Value = startValue;
        return component;
    }

    private static OptimizationSettings CreateSettings(
        Component component, int budget = 40, int topN = OptimizationSettings.DefaultTopN)
    {
        var parameter = new OptimizationParameter(component, 0, "Coupling");
        var objective = new PinPowerObjective(new[] { OutputPin }, "Power at OUT");
        return new OptimizationSettings(
            new[] { parameter }, objective, StandardWaveLengths.RedNM, budget, FixedSeed, topN);
    }

    [Fact]
    public async Task RunAsync_MziDemo_FindsVariantBetterThanBaseline()
    {
        // Start far from the sin²(π·k) optimum at k = 0.5.
        var component = CreateComponentAt(0.1);
        var optimizer = new CircuitOptimizer(CreateMziCalculator(component).Object);

        var result = await optimizer.RunAsync(CreateSettings(component));

        result.TopVariants.ShouldNotBeEmpty();
        result.TopVariants[0].Score.ShouldBeGreaterThan(result.BaselineScore);
    }

    [Fact]
    public async Task RunAsync_RespectsEvaluationBudget()
    {
        var component = CreateComponentAt(0.1);
        int evaluations = 0;
        var optimizer = new CircuitOptimizer(
            CreateMziCalculator(component, () => evaluations++).Object);

        var result = await optimizer.RunAsync(CreateSettings(component, budget: 10));

        evaluations.ShouldBe(10);
        result.EvaluationsUsed.ShouldBe(10);
    }

    [Fact]
    public async Task RunAsync_RestoresOriginalSliderValue()
    {
        var component = CreateComponentAt(0.3);
        var optimizer = new CircuitOptimizer(CreateMziCalculator(component).Object);

        await optimizer.RunAsync(CreateSettings(component));

        component.GetSlider(0)!.Value.ShouldBe(0.3);
    }

    [Fact]
    public async Task RunAsync_IsDeterministicForFixedSeed()
    {
        var component = CreateComponentAt(0.1);
        var optimizer = new CircuitOptimizer(CreateMziCalculator(component).Object);

        var first = await optimizer.RunAsync(CreateSettings(component));
        var second = await optimizer.RunAsync(CreateSettings(component));

        second.TopVariants.Count.ShouldBe(first.TopVariants.Count);
        for (int i = 0; i < first.TopVariants.Count; i++)
        {
            second.TopVariants[i].Score.ShouldBe(first.TopVariants[i].Score);
            second.TopVariants[i].ParameterValues.ShouldBe(first.TopVariants[i].ParameterValues);
        }
    }

    [Fact]
    public async Task RunAsync_Cancellation_ReturnsPartialResultAndRestoresSlider()
    {
        var component = CreateComponentAt(0.3);
        using var cts = new CancellationTokenSource();
        int evaluations = 0;
        var optimizer = new CircuitOptimizer(CreateMziCalculator(component, () =>
        {
            evaluations++;
            if (evaluations == 5) cts.Cancel();
        }).Object);

        var result = await optimizer.RunAsync(CreateSettings(component, budget: 100), cts.Token);

        result.WasCancelled.ShouldBeTrue();
        result.EvaluationsUsed.ShouldBeLessThan(100);
        component.GetSlider(0)!.Value.ShouldBe(0.3);
    }

    [Fact]
    public async Task RunAsync_TopVariants_AreSortedDescendingAndCapped()
    {
        var component = CreateComponentAt(0.05);
        var optimizer = new CircuitOptimizer(CreateMziCalculator(component).Object);

        var result = await optimizer.RunAsync(CreateSettings(component, budget: 60, topN: 3));

        result.TopVariants.Count.ShouldBeLessThanOrEqualTo(3);
        for (int i = 1; i < result.TopVariants.Count; i++)
            result.TopVariants[i].Score.ShouldBeLessThanOrEqualTo(result.TopVariants[i - 1].Score);
    }

    [Fact]
    public async Task RunAsync_FlatObjective_ReturnsNoVariants()
    {
        var component = CreateComponentAt(0.5);
        var mock = new Mock<ILightCalculator>();
        mock.Setup(c => c.CalculateFieldPropagationAsync(
                It.IsAny<CancellationTokenSource>(), It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<Guid, Complex> { { OutputPin, Complex.One } });
        var optimizer = new CircuitOptimizer(mock.Object);

        var result = await optimizer.RunAsync(CreateSettings(component, budget: 15));

        result.TopVariants.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_ReportsProgressPerEvaluation()
    {
        var component = CreateComponentAt(0.1);
        var optimizer = new CircuitOptimizer(CreateMziCalculator(component).Object);
        var reports = new List<OptimizationProgress>();
        var progress = new SynchronousProgress(reports);

        await optimizer.RunAsync(CreateSettings(component, budget: 8), progress: progress);

        reports.Count.ShouldBe(8);
        reports[^1].EvaluationsDone.ShouldBe(8);
        reports[^1].Budget.ShouldBe(8);
    }

    /// <summary>Synchronous IProgress so reports arrive before the run completes.</summary>
    private sealed class SynchronousProgress : IProgress<OptimizationProgress>
    {
        private readonly List<OptimizationProgress> _reports;
        public SynchronousProgress(List<OptimizationProgress> reports) => _reports = reports;
        public void Report(OptimizationProgress value) => _reports.Add(value);
    }
}
