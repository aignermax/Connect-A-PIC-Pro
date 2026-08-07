using CAP_Core.Analysis.CircuitOptimization;
using CAP_Core.Components.ComponentHelpers;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Analysis.CircuitOptimization;

public class OptimizationSettingsTests
{
    private static OptimizationParameter CreateParameter() =>
        new(TestComponentHelper.CreateComponentWithSlider(0, 1, 0.5), 0, "Coupling");

    private static IOptimizationObjective CreateObjective() =>
        new PinPowerObjective(new[] { Guid.NewGuid() }, "OUT");

    [Fact]
    public void Constructor_ValidArguments_SetsProperties()
    {
        var parameter = CreateParameter();

        var settings = new OptimizationSettings(
            new[] { parameter }, CreateObjective(), StandardWaveLengths.RedNM, 50, 7, 3);

        settings.EvaluationBudget.ShouldBe(50);
        settings.Seed.ShouldBe(7);
        settings.TopN.ShouldBe(3);
        settings.Parameters.ShouldBe(new[] { parameter });
    }

    [Fact]
    public void Constructor_EmptyParameters_Throws()
    {
        Should.Throw<ArgumentException>(() => new OptimizationSettings(
            Array.Empty<OptimizationParameter>(), CreateObjective(), 1550, 50, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Constructor_BudgetBelowTwo_Throws(int budget)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new OptimizationSettings(
            new[] { CreateParameter() }, CreateObjective(), 1550, budget, 0));
    }

    [Fact]
    public void Constructor_NonPositiveWavelength_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new OptimizationSettings(
            new[] { CreateParameter() }, CreateObjective(), 0, 50, 0));
    }

    [Fact]
    public void Constructor_TopNBelowOne_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new OptimizationSettings(
            new[] { CreateParameter() }, CreateObjective(), 1550, 50, 0, topN: 0));
    }

    [Fact]
    public void OptimizationParameter_TakesBoundsFromSlider()
    {
        var component = TestComponentHelper.CreateComponentWithSlider(0.2, 0.9, 0.5);

        var parameter = new OptimizationParameter(component, 0, "Coupling");

        parameter.MinValue.ShouldBe(0.2);
        parameter.MaxValue.ShouldBe(0.9);
        parameter.Clamp(1.5).ShouldBe(0.9);
        parameter.Clamp(0.0).ShouldBe(0.2);
    }

    [Fact]
    public void OptimizationParameter_InvalidSliderIndex_Throws()
    {
        var component = TestComponentHelper.CreateComponentWithSlider(0, 1, 0.5);

        Should.Throw<ArgumentException>(() => new OptimizationParameter(component, 5, "Missing"));
    }
}
