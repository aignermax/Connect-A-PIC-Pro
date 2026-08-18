using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Quantitative fan-out level report over the shipped <c>examples/Logic Gate 4-Bit
/// Adder.lun</c> (#1011 honest fan-out treatment, sites regrouped by the persisted
/// signal names of #1025/#1034): the detection groups the 261 unconnected operand
/// pins by their explicit signal name, so the 4-bit adder reports nine network-input
/// sites — A0/B0 with 49 loads each, A1/B1 with 37, A2/B2 with 25, A3/B3 with 13 and
/// Cin with 13. The counts shrink along the ripple because a stage's operand also
/// feeds its duplicated carry-OR subtrees: stage s sees 3·(3 + K_s) + K_s loads with
/// K = (10, 7, 4, 1), and the carry-in lands on the 3 + K_0 pins of stage 0's second
/// half-adder. Every site is driven at the full input power 1.0 — split ideally, each
/// branch sees 1/N ≤ 1/13 ≈ 0.077, below the NAND threshold 0.125, so no branch would
/// still read as 1: a physical build needs splitters plus level restoration per site.
/// Every site must carry one verdict per receiving input, finite and reproducible.
/// </summary>
public class LogicGateFourBitAdderFanOutLevelTests
    : IClassFixture<LogicGateFourBitAdderExampleTests.FourBitAdderFixture>
{
    private const double FullInputPower = 1.0;
    private const double NandThreshold = 0.125;

    /// <summary>The nine network-input sites with their true member counts.</summary>
    private static readonly (string Signal, int Loads)[] ExpectedSites =
    {
        ("A0", 49), ("B0", 49),
        ("A1", 37), ("B1", 37),
        ("A2", 25), ("B2", 25),
        ("A3", 13), ("B3", 13),
        ("Cin", 13),
    };

    private readonly LogicGateFourBitAdderExampleTests.FourBitAdderFixture _fixture;

    /// <summary>Attaches the shared loaded-and-assembled 4-bit adder.</summary>
    public LogicGateFourBitAdderFanOutLevelTests(LogicGateFourBitAdderExampleTests.FourBitAdderFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public void AssembledNetwork_EveryFanOutSite_CarriesAVerdictPerReceivingInput()
    {
        var warnings = _fixture.Network.FanOutWarnings;

        warnings.Count.ShouldBe(9,
            "the operand bits A0–A3, B0–B3 and the carry-in Cin are nine separate signals (#1025/#1034)");
        warnings.Select(w => w.DriverDisplayName).ShouldBe(
            ExpectedSites.Select(s => s.Signal).ToArray(), ignoreOrder: true);
        foreach (var warning in warnings)
        {
            warning.IsNetworkInputSignal.ShouldBeTrue(
                $"site '{warning.DriverDisplayName}' is a shared network-input signal");
            warning.Levels.ShouldNotBeNull($"site '{warning.DriverDisplayName}' needs its level report");
            warning.Levels.Branches.Count.ShouldBe(warning.LoadCount,
                $"site '{warning.DriverDisplayName}' needs one verdict per receiving input");
            warning.Levels.Branches.Select(b => b.LoadName).ShouldBe(warning.LoadNames, ignoreOrder: true,
                "every warned-about load carries a verdict");
            double.IsFinite(warning.Levels.DriverPowerOne).ShouldBeTrue();
            double.IsFinite(warning.Levels.BranchPower).ShouldBeTrue();
            double.IsFinite(warning.Levels.SplitLossDb).ShouldBeTrue();
            warning.Levels.Branches.ShouldAllBe(b => double.IsFinite(b.Threshold),
                "thresholds come from the persisted extractions — always finite");
            warning.Levels.Branches.ShouldAllBe(
                b => b.ReadsAsOne == (warning.Levels.BranchPower >= b.Threshold),
                "the verdict must match the extraction contract: power ≥ threshold reads as 1");
        }
    }

    [Theory]
    [InlineData("A0", 49)]
    [InlineData("B0", 49)]
    [InlineData("A1", 37)]
    [InlineData("B1", 37)]
    [InlineData("A2", 25)]
    [InlineData("B2", 25)]
    [InlineData("A3", 13)]
    [InlineData("B3", 13)]
    [InlineData("Cin", 13)]
    public void AssembledNetwork_OperandSignalsSplitIdeally_WouldFailPhysically(string signal, int expectedLoads)
    {
        var warning = _fixture.Network.FanOutWarnings.Single(w => w.DriverDisplayName == signal);

        warning.LoadCount.ShouldBe(expectedLoads);
        warning.Levels.DriverPowerOne.ShouldBe(FullInputPower,
            "a network-input signal is driven by one source at the full input power");
        warning.Levels.BranchPower.ShouldBe(FullInputPower / expectedLoads, 1e-12);
        warning.Levels.Branches.ShouldAllBe(b => !b.ReadsAsOne && b.Threshold == NandThreshold,
            $"1/{expectedLoads} ≈ {FullInputPower / expectedLoads:0.###} < {NandThreshold} — " +
            "the ideally split signal would no longer switch the receiving NAND gates");
    }

    [Fact]
    public void AssembledNetwork_CarryInLandsOnStageZeroSecondHalfAdderPinsOnly()
    {
        var warning = _fixture.Network.FanOutWarnings.Single(w => w.DriverDisplayName == "Cin");

        warning.LoadNames.ShouldAllBe(
            name => name.StartsWith("T0H2", StringComparison.Ordinal) && name.EndsWith(".A", StringComparison.Ordinal),
            "Cin feeds only stage 0's second-half-adder A pins — stages 1–3 read the previous carry through wires");
        warning.LoadNames.ShouldContain("T0H2N1A.A");
        warning.LoadNames.ShouldContain("T0H2N5C10.A",
            "the deepest duplicated carry stage of stage 0 is also a Cin load");
    }

    [Fact]
    public async Task ReassembledNetwork_ProducesIdenticalLevelReports()
    {
        var reassembled = await LogicGateFourBitAdderExampleTests.AssembleNetwork(_fixture.Canvas);

        var expected = _fixture.Network.FanOutWarnings.OrderBy(w => w.DriverDisplayName).ToList();
        var actual = reassembled.FanOutWarnings.OrderBy(w => w.DriverDisplayName).ToList();
        actual.Select(w => w.DriverDisplayName).ShouldBe(expected.Select(w => w.DriverDisplayName).ToList());
        foreach (var (first, second) in expected.Zip(actual))
        {
            second.Levels.DriverPowerOne.ShouldBe(first.Levels.DriverPowerOne);
            second.Levels.BranchPower.ShouldBe(first.Levels.BranchPower);
            second.Levels.SplitLossDb.ShouldBe(first.Levels.SplitLossDb);
            second.Levels.Branches.ShouldBe(first.Levels.Branches,
                "re-running the real extraction must reproduce the same verdicts");
        }
    }
}
