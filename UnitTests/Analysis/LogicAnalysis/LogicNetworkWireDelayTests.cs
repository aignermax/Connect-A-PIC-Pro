using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Inter-gate wire delays in the network timing (issue #1020): the critical path
/// sums gate delays AND wire delays along the path — a long wire on the otherwise
/// fast branch makes that branch the critical one. Edges the wiring does not
/// contain and implausible wire delays are rejected with messages naming the pins;
/// networks built without wire-delay data report zero wire delays.
/// </summary>
public class LogicNetworkWireDelayTests
{
    private static readonly LogicWireEdge NandToInv = new(new("nand", "Y"), new("inv", "A"));

    [Fact]
    public void CriticalPath_TwoGateChainWithWireDelay_SumsGateDelaysPlusWireDelay()
    {
        var network = Chain(
            new Dictionary<string, double> { ["nand"] = 10, ["inv"] = 20 },
            new Dictionary<LogicWireEdge, double> { [NandToInv] = 5 });

        network.WireDelaysPicoseconds[NandToInv].ShouldBe(5);
        network.CriticalPathDelayPicoseconds.ShouldBe(35);
        network.CriticalPathGateIds.ShouldBe(new[] { "nand", "inv" },
            "the wire slows the path down but does not change its gate sequence");
    }

    [Fact]
    public void CriticalPath_LongWireOnTheFastBranch_MakesThatBranchCritical()
    {
        var network = new LogicNetworkEvaluator(
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
            new Dictionary<string, double> { ["fast"] = 10, ["slow"] = 50, ["nand"] = 5 },
            new Dictionary<LogicWireEdge, double> { [new(new("fast", "Y"), new("nand", "A"))] = 100 });

        network.CriticalPathDelayPicoseconds.ShouldBe(115,
            "10 ps gate + 100 ps wire + 5 ps gate beats the wire-free 50 ps branch");
        network.CriticalPathGateIds.ShouldBe(new[] { "fast", "nand" });
    }

    [Fact]
    public void Constructor_NoWireDelayData_ReportsZeroWireDelaysForEveryEdge()
    {
        var network = Chain(new Dictionary<string, double> { ["nand"] = 10, ["inv"] = 20 }, null);

        network.WireDelaysPicoseconds.ShouldBe(
            new Dictionary<LogicWireEdge, double> { [NandToInv] = 0 });
        network.CriticalPathDelayPicoseconds.ShouldBe(30);
    }

    [Fact]
    public void Constructor_WireDelayForUnknownEdge_ThrowsNamingThePins()
    {
        var wireDelays = new Dictionary<LogicWireEdge, double> { [new(new("nand", "Y"), new("nand", "A"))] = 1 };

        var exception = Should.Throw<ArgumentException>(() => Chain(null, wireDelays));
        exception.Message.ShouldContain("nand.Y");
        exception.Message.ShouldContain("nand.A");
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_ImplausibleWireDelay_ThrowsNamingThePins(double delay)
    {
        var wireDelays = new Dictionary<LogicWireEdge, double> { [NandToInv] = delay };

        var exception = Should.Throw<ArgumentException>(() => Chain(null, wireDelays));
        exception.Message.ShouldContain("nand.Y");
        exception.Message.ShouldContain("inv.A");
    }

    /// <summary>The AND-from-NAND chain: a, b → nand → inv → y, with optional per-edge wire delays.</summary>
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
}
