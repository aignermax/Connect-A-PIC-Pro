using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Per-gate switch events with arrival times (issue #1035, rung 4→5 groundwork):
/// a chain accumulates gate and wire delays along the events; a join gate waits
/// for the slower of its two arrivals; an input assignment that changes nothing
/// produces an empty timeline; a stable mid-chain driver is treated as arrival 0
/// so a downstream gate still switches off the changed branch alone; ties on
/// time are broken deterministically by gate id. When the switching path IS the
/// critical path, the last event lands exactly on the critical-path delay.
/// </summary>
public class LogicEventTimelineTests
{
    private static readonly LogicWireEdge NandToInv = new(new("nand", "Y"), new("inv", "A"));

    [Fact]
    public void Compute_Chain_AccumulatesGateAndWireDelaysAlongTheEvents()
    {
        var network = Chain(
            new Dictionary<string, double> { ["nand"] = 10, ["inv"] = 20 },
            new Dictionary<LogicWireEdge, double> { [NandToInv] = 5 });

        var events = LogicEventTimeline.Compute(
            network,
            Bits(("a", true), ("b", true)),
            Bits(("a", false), ("b", true)));

        events.ShouldBe(new[]
        {
            new LogicSwitchEvent(10, "nand", "Y", true),
            new LogicSwitchEvent(35, "inv", "Y", false),
        });
        events[^1].TimePicoseconds.ShouldBe(network.CriticalPathDelayPicoseconds,
            "the switching path IS the critical path: the last event lands exactly on it");
    }

    [Fact]
    public void Compute_ForkJoin_JoinGateWaitsForTheSlowerArrival()
    {
        var network = ForkJoin(
            new Dictionary<string, double> { ["fast"] = 10, ["slow"] = 50, ["nand"] = 5 },
            new Dictionary<LogicWireEdge, double> { [new(new("fast", "Y"), new("nand", "A"))] = 7 });

        var events = LogicEventTimeline.Compute(
            network,
            Bits(("a", false), ("b", false)),
            Bits(("a", true), ("b", true)));

        events.ShouldBe(new[]
        {
            new LogicSwitchEvent(10, "fast", "Y", false),
            new LogicSwitchEvent(50, "slow", "Y", false),
            new LogicSwitchEvent(55, "nand", "Y", true),
        }, "the fast branch arrives at 17 ps but the join waits for the slow branch's 50 ps");
        events[^1].TimePicoseconds.ShouldBe(network.CriticalPathDelayPicoseconds);
    }

    [Fact]
    public void Compute_NoChangeInput_EmptyTimeline()
    {
        var network = Chain(
            new Dictionary<string, double> { ["nand"] = 10, ["inv"] = 20 },
            new Dictionary<LogicWireEdge, double> { [NandToInv] = 5 });

        LogicEventTimeline.Compute(
            network,
            Bits(("a", true), ("b", true)),
            Bits(("a", true), ("b", true))).ShouldBeEmpty();

        LogicEventTimeline.Compute(
            network,
            Bits(("a", true), ("b", false)),
            Bits(("a", true), ("b", false))).ShouldBeEmpty(
            "toggling no input leaves every gate output at its previous level");
    }

    [Fact]
    public void Compute_JoinAbsorbsTheToggle_OnlyTheFirstGateFires()
    {
        var network = ForkJoin(
            new Dictionary<string, double> { ["fast"] = 10, ["slow"] = 50, ["nand"] = 5 },
            null);

        var events = LogicEventTimeline.Compute(
            network,
            Bits(("a", false), ("b", true)),
            Bits(("a", true), ("b", true)));

        events.ShouldBe(new[]
        {
            new LogicSwitchEvent(10, "fast", "Y", false),
        }, "slow is false either way, so NAND(·, false) = true absorbs fast's flip — the timeline stops");
    }

    [Fact]
    public void Compute_JoinWithOneStableInput_SwitchesOffTheChangedBranchAlone()
    {
        var network = ForkJoin(
            new Dictionary<string, double> { ["fast"] = 10, ["slow"] = 50, ["nand"] = 5 },
            null);

        var events = LogicEventTimeline.Compute(
            network,
            Bits(("a", false), ("b", false)),
            Bits(("a", true), ("b", false)));

        events.ShouldBe(new[]
        {
            new LogicSwitchEvent(10, "fast", "Y", false),
            new LogicSwitchEvent(15, "nand", "Y", true),
        }, "slow never switches — its stable arrival of 0 lets the join fire off the fast branch");
    }

    [Fact]
    public void Compute_SimultaneousEvents_TiesBrokenByGateId()
    {
        var network = ForkJoin(
            new Dictionary<string, double> { ["fast"] = 10, ["slow"] = 10, ["nand"] = 5 },
            null);

        var events = LogicEventTimeline.Compute(
            network,
            Bits(("a", false), ("b", false)),
            Bits(("a", true), ("b", true)));

        events.Take(2).ShouldBe(new[]
        {
            new LogicSwitchEvent(10, "fast", "Y", false),
            new LogicSwitchEvent(10, "slow", "Y", false),
        }, "same switch time → deterministic order by gate id");
        events[^1].ShouldBe(new LogicSwitchEvent(15, "nand", "Y", true));
    }

    [Fact]
    public void Compute_MissingInputBit_ThrowsNamingThePin()
    {
        var network = Chain(null, null);

        var exception = Should.Throw<ArgumentException>(() =>
            LogicEventTimeline.Compute(network, Bits(("a", true)), Bits(("a", true), ("b", true))));
        exception.Message.ShouldContain("b");
    }

    [Fact]
    public void Compute_NullArguments_Throw()
    {
        var network = Chain(null, null);

        Should.Throw<ArgumentNullException>(() =>
            LogicEventTimeline.Compute(null!, Bits(("a", true), ("b", true)), Bits(("a", true), ("b", true))));
        Should.Throw<ArgumentNullException>(() =>
            LogicEventTimeline.Compute(network, null!, Bits(("a", true), ("b", true))));
        Should.Throw<ArgumentNullException>(() =>
            LogicEventTimeline.Compute(network, Bits(("a", true), ("b", true)), null!));
    }

    /// <summary>The AND-from-NAND chain: a, b → nand → inv → y.</summary>
    private static LogicNetworkEvaluator Chain(
        IReadOnlyDictionary<string, double>? gateDelays,
        IReadOnlyDictionary<LogicWireEdge, double>? wireDelays) =>
        new(
            new[] { "a", "b" },
            new Dictionary<string, LogicGateModel>
            {
                ["nand"] = PinnedGateTables.NandGate(),
                ["inv"] = PinnedGateTables.NotGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("nand", "B")] = new LogicNetDriver.NetworkInput("b"),
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("nand", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("inv", "Y") },
            gateDelays,
            wireDelays);

    /// <summary>Two NOTs fanning in to one NAND: a → fast, b → slow, (fast, slow) → nand → y.</summary>
    private static LogicNetworkEvaluator ForkJoin(
        IReadOnlyDictionary<string, double>? gateDelays,
        IReadOnlyDictionary<LogicWireEdge, double>? wireDelays) =>
        new(
            new[] { "a", "b" },
            new Dictionary<string, LogicGateModel>
            {
                ["fast"] = PinnedGateTables.NotGate(),
                ["slow"] = PinnedGateTables.NotGate(),
                ["nand"] = PinnedGateTables.NandGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("fast", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("slow", "A")] = new LogicNetDriver.NetworkInput("b"),
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("fast", "Y")),
                [new LogicPinRef("nand", "B")] = new LogicNetDriver.GateOutput(new LogicPinRef("slow", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("nand", "Y") },
            gateDelays,
            wireDelays);

    /// <summary>One dictionary of network input bits.</summary>
    private static Dictionary<string, bool> Bits(params (string Name, bool Bit)[] bits) =>
        bits.ToDictionary(pair => pair.Name, pair => pair.Bit);
}
