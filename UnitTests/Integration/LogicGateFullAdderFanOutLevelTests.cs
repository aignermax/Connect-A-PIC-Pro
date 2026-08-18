using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Quantitative fan-out level report over the shipped <c>examples/Logic Gate Full
/// Adder.lun</c> (#1011, rung 4 honest fan-out treatment): the detection groups
/// unconnected gate inputs by pin name, so the addend-A pins and the carry-in pins
/// (all named <c>A</c>) form one 17-load site and the addend-B pins one 13-load
/// site. Both are network-input signals driven at the full input power 1.0 — split
/// ideally, each branch would see only 1/17 ≈ 0.059 or 1/13 ≈ 0.077, far below the
/// NAND threshold 0.125: none of these wires would work physically without splitters
/// and level restoration. Every site must carry one verdict per receiving input,
/// with finite, reproducible values.
/// </summary>
public class LogicGateFullAdderFanOutLevelTests : IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private const double FullInputPower = 1.0;
    private const double NandThreshold = 0.125;
    private const int LoadsOfSignalA = 17;
    private const int LoadsOfSignalB = 13;

    private readonly LogicGateFullAdderExampleTests.FullAdderFixture _fixture;

    /// <summary>Attaches the shared loaded-and-assembled full adder.</summary>
    public LogicGateFullAdderFanOutLevelTests(LogicGateFullAdderExampleTests.FullAdderFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public void AssembledNetwork_EveryFanOutSite_CarriesAVerdictPerReceivingInput()
    {
        var warnings = _fixture.Network.FanOutWarnings;

        warnings.Count.ShouldBe(2, "the unconnected inputs carry pin names A and B — two shared signals");
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
    [InlineData("A", LoadsOfSignalA)]
    [InlineData("B", LoadsOfSignalB)]
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
    public async Task ReassembledNetwork_ProducesIdenticalLevelReports()
    {
        var reassembled = await LogicGateFullAdderExampleTests.AssembleNetwork(_fixture.Canvas);

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
