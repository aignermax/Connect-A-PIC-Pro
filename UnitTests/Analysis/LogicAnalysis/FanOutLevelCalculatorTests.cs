using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Quantitative fan-out level report (#1011, rung 4 honest fan-out treatment): an
/// ideal 1×N splitter behind the driver hands every branch P_out/N (−10·log10(N)
/// dB — N=2 halves the power, 3.01 dB), and every receiving input is checked
/// against its gate's pinned power threshold: branch power ≥ threshold still reads
/// as a logic 1, below it the wire would fail physically. The driving gate's 1-level
/// is the weakest logic-1 power of its truth table; a network-input signal delivers
/// the full input power. Single-load (N=1) sites never split and produce no report.
/// </summary>
public class FanOutLevelCalculatorTests
{
    private static readonly LogicPinRef SourceOutput = new("SRC", "Y");

    [Fact]
    public void BranchPower_TwoBranches_HalvesThePower()
    {
        FanOutLevelCalculator.BranchPower(0.8, 2).ShouldBe(0.4, 1e-12);
        FanOutLevelCalculator.SplitLossDb(2).ShouldBe(3.0103, 0.001,
            "an ideal 1×2 split costs −10·log10(2) ≈ 3.01 dB per branch");
    }

    [Fact]
    public void ForGateOutput_DriverPowerIsWeakestLogicOneLevelOfTruthTable()
    {
        // The pinned NAND emits 1-levels of 0.5/0.25/0.25 — the weakest (0.25) is the
        // conservative driver level; split over two branches that lands at 0.125.
        var calculator = Calculator(("SRC", PinnedGateTables.NandGate()), ("LOAD", PinnedGateTables.NandGate()));

        var report = calculator.ForGateOutput(SourceOutput, Loads("LOAD", "A", "B"));

        report.DriverPowerOne.ShouldBe(0.25);
        report.BranchPower.ShouldBe(0.125);
        report.SplitLossDb.ShouldBe(3.0103, 0.001);
        report.Branches.Count.ShouldBe(2);
    }

    [Fact]
    public void ForGateOutput_ThresholdJustBelowBranchPower_StillReadsAsOne()
    {
        // P/N = 0.5/2 = 0.25; a receiving threshold of 0.249 sits just below.
        var calculator = Calculator(("SRC", PinnedGateTables.NotGate()), ("LOAD", NotGateWithThreshold(0.249)));

        var report = calculator.ForGateOutput(SourceOutput, Loads("LOAD", "A", "A"));

        report.BranchPower.ShouldBe(0.25);
        report.Branches.ShouldAllBe(branch => branch.ReadsAsOne,
            "0.25 ≥ 0.249 — the branch power still reaches the threshold");
    }

    [Fact]
    public void ForGateOutput_ThresholdJustAboveBranchPower_WouldFail()
    {
        // P/N = 0.5/2 = 0.25; a receiving threshold of 0.251 sits just above.
        var calculator = Calculator(("SRC", PinnedGateTables.NotGate()), ("LOAD", NotGateWithThreshold(0.251)));

        var report = calculator.ForGateOutput(SourceOutput, Loads("LOAD", "A", "A"));

        report.BranchPower.ShouldBe(0.25);
        report.Branches.ShouldAllBe(branch => !branch.ReadsAsOne,
            "0.25 < 0.251 — the branch power falls below the threshold");
    }

    [Fact]
    public void ForNetworkInput_DriverDeliversFullInputPower()
    {
        var calculator = Calculator(("LOAD", PinnedGateTables.NotGate()));

        var report = calculator.ForNetworkInput(Loads("LOAD", "A", "A"));

        report.DriverPowerOne.ShouldBe(FanOutLevelCalculator.NetworkInputPowerOne);
        report.BranchPower.ShouldBe(0.5);
        report.Branches.ShouldAllBe(branch => branch.ReadsAsOne && branch.Threshold == 0.375,
            "0.5 ≥ 0.375 — halving the full input power still reaches the NOT threshold");
    }

    [Fact]
    public void ForNetworkInput_SingleLoad_ProducesNoReport()
    {
        var calculator = Calculator(("LOAD", PinnedGateTables.NotGate()));

        Should.Throw<ArgumentException>(() => calculator.ForNetworkInput(Loads("LOAD", "A")))
            .Message.ShouldContain("at least two loads");
    }

    [Fact]
    public void Evaluator_PointToPointNetwork_ProducesNoFanOutReport()
    {
        var gates = new Dictionary<string, LogicGateModel>
        {
            ["G1"] = PinnedGateTables.NotGate(),
            ["G2"] = PinnedGateTables.NotGate(),
        };
        var wiring = new Dictionary<LogicPinRef, LogicNetDriver>
        {
            [new LogicPinRef("G1", "A")] = new LogicNetDriver.NetworkInput("G1.A"),
            [new LogicPinRef("G2", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("G1", "Y")),
        };
        var taps = new Dictionary<string, LogicPinRef>
        {
            ["G1.Y"] = new("G1", "Y"),
            ["G2.Y"] = new("G2", "Y"),
        };

        var network = new LogicNetworkEvaluator(new[] { "G1.A" }, gates, wiring, taps);

        network.FanOutWarnings.ShouldBeEmpty(
            "every wire is one output to one input — N=1 sites produce no report");
    }

    [Fact]
    public void Evaluator_FanOutWarning_CarriesLevelReportPerReceivingInput()
    {
        var gates = new Dictionary<string, LogicGateModel>
        {
            ["SRC"] = PinnedGateTables.NotGate(),
            ["LOAD"] = PinnedGateTables.NandGate(),
        };
        var wiring = new Dictionary<LogicPinRef, LogicNetDriver>
        {
            [new LogicPinRef("SRC", "A")] = new LogicNetDriver.NetworkInput("SRC.A"),
            [new LogicPinRef("LOAD", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("SRC", "Y")),
            [new LogicPinRef("LOAD", "B")] = new LogicNetDriver.GateOutput(new LogicPinRef("SRC", "Y")),
        };
        var taps = new Dictionary<string, LogicPinRef>
        {
            ["SRC.Y"] = new("SRC", "Y"),
            ["LOAD.Y"] = new("LOAD", "Y"),
        };

        var network = new LogicNetworkEvaluator(new[] { "SRC.A" }, gates, wiring, taps);

        var warning = network.FanOutWarnings.ShouldHaveSingleItem();
        warning.Levels.DriverPowerOne.ShouldBe(0.5, "the pinned NOT's weakest 1-level");
        warning.Levels.BranchPower.ShouldBe(0.25);
        warning.Levels.Branches.Select(b => b.LoadName).ShouldBe(new[] { "LOAD.A", "LOAD.B" });
        warning.Levels.Branches.ShouldAllBe(b => b.ReadsAsOne && b.Threshold == 0.125,
            "0.25 ≥ 0.125 — both NAND inputs would still read a 1 after the ideal split");
    }

    /// <summary>A calculator over a synthetic network of (gateId, model) pairs.</summary>
    private static FanOutLevelCalculator Calculator(params (string Id, LogicGateModel Model)[] gates) =>
        new(gates.ToDictionary(gate => gate.Id, gate => gate.Model));

    /// <summary>Load pin refs: the same input pin of one gate once per requested load.</summary>
    private static LogicPinRef[] Loads(string gateId, params string[] pinNames)
    {
        var loads = new List<LogicPinRef>();
        foreach (var pinName in pinNames)
        {
            loads.Add(new LogicPinRef(gateId, pinName));
        }
        return loads.ToArray();
    }

    /// <summary>A NOT-shaped gate model (one input A, one output Y) at a freely chosen threshold.</summary>
    private static LogicGateModel NotGateWithThreshold(double threshold)
    {
        var rows = new[]
        {
            new TruthTableRow(
                new Dictionary<string, bool> { ["A"] = false },
                new Dictionary<string, LogicOutputValue> { ["Y"] = new(true, 0.5) }),
            new TruthTableRow(
                new Dictionary<string, bool> { ["A"] = true },
                new Dictionary<string, LogicOutputValue> { ["Y"] = new(false, 0.25) }),
        };
        return LogicGateModel.FromTruthTable(
            new TruthTable("Synthetic NOT", new[] { "A" }, new[] { "Y" }, threshold, PinnedGateTables.WavelengthNm, rows));
    }
}
