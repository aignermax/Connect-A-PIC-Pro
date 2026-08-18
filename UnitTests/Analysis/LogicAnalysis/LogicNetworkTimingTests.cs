using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Critical-path timing over the gate DAG (issue #1002): the longest cumulative
/// propagation delay from any network input to any output. A hand-built two-gate
/// chain sums both gate delays; a fan-in network follows the slower branch;
/// networks built without delay data report zero delays; implausible delay data
/// is rejected with a message naming the gate.
/// </summary>
public class LogicNetworkTimingTests
{
    [Fact]
    public void CriticalPath_TwoGateChain_SumsBothGateDelays()
    {
        var network = Chain(new Dictionary<string, double> { ["nand"] = 10, ["inv"] = 20 });

        network.GateDelaysPicoseconds["nand"].ShouldBe(10);
        network.GateDelaysPicoseconds["inv"].ShouldBe(20);
        network.CriticalPathDelayPicoseconds.ShouldBe(30);
        network.CriticalPathGateIds.ShouldBe(new[] { "nand", "inv" });
    }

    [Fact]
    public void CriticalPath_FanInNetwork_FollowsTheSlowerBranch()
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
            new Dictionary<string, double> { ["fast"] = 10, ["slow"] = 50, ["nand"] = 5 });

        network.CriticalPathDelayPicoseconds.ShouldBe(55);
        network.CriticalPathGateIds.ShouldBe(new[] { "slow", "nand" });
    }

    [Fact]
    public void CriticalPath_NoDelayData_ReportsZeroDelays()
    {
        var network = Chain(null);

        network.GateDelaysPicoseconds.Values.ShouldAllBe(delay => delay == 0);
        network.CriticalPathDelayPicoseconds.ShouldBe(0);
        network.CriticalPathGateIds.ShouldBe(new[] { "inv" },
            "the only output tap: a zero-delay chain of one gate");
    }

    [Fact]
    public void Constructor_DelayForUnknownGate_ThrowsNamingTheGate()
    {
        var delays = new Dictionary<string, double> { ["ghost"] = 1 };

        var exception = Should.Throw<ArgumentException>(() => Chain(delays));
        exception.Message.ShouldContain("ghost");
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_ImplausibleDelay_ThrowsNamingTheGate(double delay)
    {
        var delays = new Dictionary<string, double> { ["nand"] = delay };

        var exception = Should.Throw<ArgumentException>(() => Chain(delays));
        exception.Message.ShouldContain("nand");
    }

    /// <summary>The AND-from-NAND chain: a, b → nand → inv → y.</summary>
    private static LogicNetworkEvaluator Chain(IReadOnlyDictionary<string, double>? delays) =>
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
            delays);
}
