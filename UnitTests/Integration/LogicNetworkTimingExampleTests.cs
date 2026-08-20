using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Propagation delays and the critical path on the shipped half-adder example
/// (issue #1002, wire delays #1020): the assembled network carries a non-zero,
/// finite delay per gate derived from the group's internal optical path length with
/// the documented formula (delay = L · n_g / c, default silicon group index), and the
/// critical path equals the sum of gate delays plus inter-gate wire delays along its
/// ordered gate chain.
/// </summary>
public class LogicNetworkTimingExampleTests : IClassFixture<LogicPanelViewModelTests.LoadedHalfAdder>
{
    private readonly LogicPanelViewModelTests.LoadedHalfAdder _fixture;

    /// <summary>Attaches the shared loaded half-adder canvas.</summary>
    public LogicNetworkTimingExampleTests(LogicPanelViewModelTests.LoadedHalfAdder fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task AssembleNetwork_HalfAdder_ReportsNonZeroFiniteDelaysAndCriticalPath()
    {
        var network = await AssembleHalfAdder();

        network.GateDelaysPicoseconds.Values.ShouldAllBe(
            delay => delay > 0 && double.IsFinite(delay),
            "every gate has a non-zero, finite propagation delay");
        network.CriticalPathDelayPicoseconds.ShouldBeGreaterThan(0);
        double.IsFinite(network.CriticalPathDelayPicoseconds).ShouldBeTrue();
        network.CriticalPathGateIds.Count.ShouldBeGreaterThan(1,
            "the half adder's critical path is a chain of gates, not a single gate");
        network.CriticalPathDelayPicoseconds.ShouldBe(
            GateDelaySum(network) + WireDelaySumAlongPath(network), 1e-9,
            "the critical path sums gate delays and inter-gate wire delays along its gate chain");
    }

    /// <summary>The sum of the gate delays along the critical path's gate chain.</summary>
    private static double GateDelaySum(LogicNetworkEvaluator network) =>
        network.CriticalPathGateIds.Sum(id => network.GateDelaysPicoseconds[id]);

    /// <summary>
    /// The wire delays the critical path picks up between consecutive gates of its
    /// chain: for each hop, the slowest wire from the previous gate into the next
    /// (a gate driven twice by the same predecessor pays only the slower wire).
    /// </summary>
    private static double WireDelaySumAlongPath(LogicNetworkEvaluator network)
    {
        var path = network.CriticalPathGateIds;
        var sum = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            sum += network.WireDelaysPicoseconds
                .Where(pair => pair.Key.Source.GateId == path[i - 1] && pair.Key.Load.GateId == path[i])
                .Select(pair => pair.Value)
                .DefaultIfEmpty(0)
                .Max();
        }
        return sum;
    }

    [Fact]
    public async Task AssembleNetwork_HalfAdder_GateDelaysFollowTheDocumentedFormula()
    {
        var network = await AssembleHalfAdder();

        foreach (var group in GateGroups())
        {
            var expected = GateDelayCalculator.InternalPathLengthMicrometers(group)
                * GateDelayCalculator.DefaultGroupIndex
                / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond;
            network.GateDelaysPicoseconds[group.GroupName].ShouldBe(expected, 1e-9,
                $"gate '{group.GroupName}': delay = internal path length × n_g / c");
        }
    }

    /// <summary>Assembles the half adder's logic network like the Logic panel does.</summary>
    private async Task<LogicNetworkEvaluator> AssembleHalfAdder() =>
        await new LogicNetworkAssembler().AssembleAsync(
            _fixture.Canvas.Components.Select(c => c.Component).ToList(),
            _fixture.Canvas.Connections.Select(c => c.Connection).ToList(),
            StandardWaveLengths.RedNM);

    /// <summary>The loaded design's top-level gate groups.</summary>
    private IEnumerable<ComponentGroup> GateGroups() =>
        _fixture.Canvas.Components.Select(c => c.Component)
            .OfType<ComponentGroup>()
            .Where(group => group.TruthTablePinAssignment != null);
}
