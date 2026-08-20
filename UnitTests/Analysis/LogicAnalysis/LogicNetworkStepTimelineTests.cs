using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The clock step as a timeline source (issue #1110, rung 5 visualizer):
/// <see cref="LogicNetworkEvaluator.Step"/> returns the entries the execution
/// visualizer appends — one commit entry per register output pin that changed
/// (at the clock edge, t = 0) and the downstream ripple of the post-commit
/// settling after it, all relative to the edge. Covered at the net-list level:
/// a D-flip-flop (commit only), the register + inverter toggle loop (commit plus
/// ripple with real delays), the cross-coupled NAND SR latch (both commits land
/// on the same edge), the quiet clock (no change, no entries), and the purely
/// combinational network (never any clock entries).
/// </summary>
public class LogicNetworkStepTimelineTests
{
    private const double InverterGateDelayPs = 5.0;
    private const double RegisterToInverterWirePs = 2.0;

    [Fact]
    public void Step_DFlipFlop_CommitsTheSampledInputAsOneEdgeEntry()
    {
        var network = DFlipFlop();
        network.Evaluate(Bits(("d", true)));

        var entries = network.Step();

        entries.ShouldBe(new[] { new LogicSwitchEvent(0.0, "reg", "Y", true) },
            "the D-flip-flop's commit lands at the clock edge — no downstream logic, no ripple");
    }

    [Fact]
    public void Step_ToggleLoop_CommitEntryPrecedesTheRippleWithArrivalTimes()
    {
        var network = ToggleLoop();

        network.Evaluate(Bits(("x", false)));
        var first = network.Step();

        first.ShouldBe(new[]
            {
                new LogicSwitchEvent(0.0, "reg", "Y", true),
                new LogicSwitchEvent(RegisterToInverterWirePs + InverterGateDelayPs, "inv", "Y", false),
            },
            "the register commits at the edge; the inverter follows after wire + gate delay");

        var second = network.Step();
        second.ShouldBe(new[]
            {
                new LogicSwitchEvent(0.0, "reg", "Y", false),
                new LogicSwitchEvent(RegisterToInverterWirePs + InverterGateDelayPs, "inv", "Y", true),
            },
            "the next clock flips the loop back — again commit first, ripple after");
    }

    [Fact]
    public void Step_SrLatch_BothCrossCoupledCommitsLandOnTheSameEdge()
    {
        var network = SrLatch();
        network.Evaluate(Bits(("s", false), ("r", true)));

        var first = network.Step();
        first.ShouldBe(new[]
            {
                new LogicSwitchEvent(0.0, "qGate", "Y", true),
                new LogicSwitchEvent(0.0, "qbGate", "Y", true),
            },
            "both NAND registers sample the powered-up 0/0 and commit 1 — the latch's transient");

        var second = network.Step();
        second.ShouldBe(new[] { new LogicSwitchEvent(0.0, "qbGate", "Y", false) },
            "the second clock settles the latch: Q̄ falls while Q holds (NAND(S̄=0, …) = 1 is unchanged)");
    }

    [Fact]
    public void Step_QuietClock_RecordsNoEntriesButKeepsTheState()
    {
        var network = SrLatch();
        network.Evaluate(Bits(("s", false), ("r", true)));
        network.Step();
        network.Step();

        network.Step().ShouldBeEmpty("holding inputs change nothing — a quiet clock has nothing to record");
        network.Step().ShouldBeEmpty();
        network.Evaluate(Bits(("s", true), ("r", true)))["q"].ShouldBeTrue("the latch still holds its state");
    }

    [Fact]
    public void Step_PurelyCombinationalNetwork_NeverProducesClockEntries()
    {
        var network = new LogicNetworkEvaluator(
            new[] { "d" },
            new Dictionary<string, LogicGateModel> { ["inv"] = PinnedGateTables.NotGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.NetworkInput("d"),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("inv", "Y") });
        network.Evaluate(Bits(("d", true)));

        network.Step().ShouldBeEmpty("no registers — clocking is meaningless");
        network.Step().ShouldBeEmpty();
    }

    /// <summary>One buffer register with D as its only network input and Q as its tap.</summary>
    private static LogicNetworkEvaluator DFlipFlop() =>
        new(
            new[] { "d" },
            new Dictionary<string, LogicGateModel> { ["reg"] = BufferGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("reg", "A")] = new LogicNetDriver.NetworkInput("d"),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("reg", "Y") },
            registerGateIds: new[] { "reg" });

    /// <summary>
    /// The toggle loop reg = NOT(reg) once clocked, with a real delay on the
    /// register → inverter wire and on the inverter itself, so the ripple's
    /// arrival times are exercised (the declared input "x" drives nothing, but
    /// Evaluate still requires its bit).
    /// </summary>
    private static LogicNetworkEvaluator ToggleLoop() =>
        new(
            new[] { "x" },
            new Dictionary<string, LogicGateModel>
            {
                ["reg"] = BufferGate(),
                ["inv"] = PinnedGateTables.NotGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("reg", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("inv", "Y")),
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("reg", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("reg", "Y") },
            gateDelays: new Dictionary<string, double> { ["inv"] = InverterGateDelayPs },
            wireDelays: new Dictionary<LogicWireEdge, double>
            {
                [new LogicWireEdge(new LogicPinRef("reg", "Y"), new LogicPinRef("inv", "A"))] =
                    RegisterToInverterWirePs,
            },
            registerGateIds: new[] { "reg" });

    /// <summary>
    /// Two cross-coupled NAND gates, both designated registers: Q = NAND(S̄, Q̄),
    /// Q̄ = NAND(R̄, Q) with active-low set/reset — the classic SR latch.
    /// </summary>
    private static LogicNetworkEvaluator SrLatch() =>
        new(
            new[] { "s", "r" },
            new Dictionary<string, LogicGateModel>
            {
                ["qGate"] = PinnedGateTables.NandGate(),
                ["qbGate"] = PinnedGateTables.NandGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("qGate", "A")] = new LogicNetDriver.NetworkInput("s"),
                [new LogicPinRef("qGate", "B")] = new LogicNetDriver.GateOutput(new LogicPinRef("qbGate", "Y")),
                [new LogicPinRef("qbGate", "A")] = new LogicNetDriver.NetworkInput("r"),
                [new LogicPinRef("qbGate", "B")] = new LogicNetDriver.GateOutput(new LogicPinRef("qGate", "Y")),
            },
            new Dictionary<string, LogicPinRef>
            {
                ["q"] = new("qGate", "Y"),
                ["qb"] = new("qbGate", "Y"),
            },
            registerGateIds: new[] { "qGate", "qbGate" });

    /// <summary>The D-flip-flop's combinational core: a buffer, Y = A.</summary>
    private static LogicGateModel BufferGate()
    {
        var inputs = new[] { "A" };
        var rows = new[] { false, true }
            .Select(bit => new TruthTableRow(
                new Dictionary<string, bool> { ["A"] = bit },
                new Dictionary<string, LogicOutputValue> { ["Y"] = new(bit, bit ? 0.5 : 0.0) }))
            .ToArray();
        return LogicGateModel.FromTruthTable(
            new TruthTable("Buffer", inputs, new[] { "Y" }, 0.25, PinnedGateTables.WavelengthNm, rows));
    }

    /// <summary>Builds an input-bit dictionary from (name, bit) pairs.</summary>
    private static Dictionary<string, bool> Bits(params (string Name, bool Bit)[] bits) =>
        bits.ToDictionary(pair => pair.Name, pair => pair.Bit);
}
