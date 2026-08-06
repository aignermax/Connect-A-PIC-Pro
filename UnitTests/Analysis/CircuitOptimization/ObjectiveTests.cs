using CAP_Core.Analysis.CircuitOptimization;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.CircuitOptimization;

public class ObjectiveTests
{
    [Fact]
    public void PinPowerObjective_SumsOnlySelectedPins()
    {
        var pinA = Guid.NewGuid();
        var pinB = Guid.NewGuid();
        var objective = new PinPowerObjective(new[] { pinA }, "OUT-A");

        double score = objective.Score(new Dictionary<Guid, double>
        {
            { pinA, 0.4 }, { pinB, 0.5 }
        });

        score.ShouldBe(0.4);
    }

    [Fact]
    public void PinPowerObjective_Minimize_NegatesScore()
    {
        var pin = Guid.NewGuid();
        var objective = new PinPowerObjective(new[] { pin }, "OUT", maximize: false);

        objective.Score(new Dictionary<Guid, double> { { pin, 0.7 } }).ShouldBe(-0.7);
    }

    [Fact]
    public void PinPowerObjective_MissingPin_ScoresZero()
    {
        var objective = new PinPowerObjective(new[] { Guid.NewGuid() }, "OUT");

        objective.Score(new Dictionary<Guid, double>()).ShouldBe(0);
    }

    [Fact]
    public void PinPowerObjective_EmptyPinSet_Throws()
    {
        Should.Throw<ArgumentException>(
            () => new PinPowerObjective(Array.Empty<Guid>(), "OUT"));
    }

    [Fact]
    public void TotalPowerObjective_SumsAllPins()
    {
        var objective = new TotalPowerObjective("Total");

        double score = objective.Score(new Dictionary<Guid, double>
        {
            { Guid.NewGuid(), 0.25 }, { Guid.NewGuid(), 0.5 }
        });

        score.ShouldBe(0.75);
    }

    [Fact]
    public void TotalPowerObjective_Minimize_NegatesScore()
    {
        var objective = new TotalPowerObjective("Loss", maximize: false);

        objective.Score(new Dictionary<Guid, double> { { Guid.NewGuid(), 0.3 } }).ShouldBe(-0.3);
    }
}
