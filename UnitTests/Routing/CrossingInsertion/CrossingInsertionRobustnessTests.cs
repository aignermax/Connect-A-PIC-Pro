using CAP_Core.Routing.CrossingInsertion;
using Shouldly;

namespace UnitTests.Routing.CrossingInsertion;

/// <summary>
/// Robustness of the crossing-insertion pass (Issue #553 review, Finding 1): a crossing
/// template missing a wired port would pass the W/E-only through-loss guard but throw
/// during placement, mid grid-mutation. The pass must reject such a template up front and
/// keep detours instead — never throwing, never corrupting the grid.
/// </summary>
public class CrossingInsertionRobustnessTests
{
    [Fact]
    public void HasAllFourWiredPorts_FullCrossing_ReturnsTrue()
    {
        var crossing = CrossingTestCircuit.CreateCrossingComponent();
        new CrossingInserter().HasAllFourWiredPorts(crossing).ShouldBeTrue();
    }

    [Fact]
    public void HasAllFourWiredPorts_MissingPort_ReturnsFalse()
    {
        var crossing = CrossingTestCircuit.CreateCrossingComponent();
        // Drop the south port — CrossingPlacement.RequirePin would throw on it.
        crossing.PhysicalPins.Remove(crossing.PhysicalPins.First(p => p.Name == "port 4"));

        new CrossingInserter().HasAllFourWiredPorts(crossing).ShouldBeFalse();
    }

    [Fact]
    public void CrossingPass_MalformedTemplate_NoInsertionAndNoThrow()
    {
        // Expensive detour so a crossing WOULD be inserted if the template were valid.
        var layout = CrossingTestCircuit.Build(
            bendLossDbPer90Deg: 0.5,
            crossingFactory: () =>
            {
                var crossing = CrossingTestCircuit.CreateCrossingComponent();
                crossing.PhysicalPins.Remove(crossing.PhysicalPins.First(p => p.Name == "port 4"));
                return crossing;
            });

        // Build() already routed + ran the crossing pass via AddConnection — it must not have
        // thrown, inserted nothing, and left both original nets intact.
        layout.AddedCrossings.ShouldBeEmpty();
        layout.Service.Records.ShouldBeEmpty();
        layout.Manager.Connections.Count.ShouldBe(2);
    }
}
