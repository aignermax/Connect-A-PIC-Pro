using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Display-wrapper tests for the fan-out level report strings (issue #1011): the
/// ViewModel formats the split line from the report (split count, driver power,
/// per-branch power, split loss in dB) and picks the pass/fail verdict key per
/// receiving input. The panel test only exercises the all-pass half adder — these
/// cover the would-fail path, culture-proofed by building the expected strings
/// through the same translation keys.
/// </summary>
public class LogicFanOutWarningViewModelTests
{
    [Fact]
    public void GateOutputWarning_FormatsWarningAndSplitLine_FromTheReport()
    {
        var warning = TwoLoadWarning(
            driverDisplayName: "SRC.Y", isNetworkInputSignal: false,
            new FanOutBranchLevel("NAND.A", 0.125, true),
            new FanOutBranchLevel("NAND.B", 0.125, true));

        var vm = new LogicFanOutWarningViewModel(warning);

        vm.WarningText.ShouldBe(string.Format(Translate("LogicPanel.FanOutWarning.GateLine"), "SRC.Y", 2));
        vm.SplitLine.ShouldBe(string.Format(
            Translate("LogicPanel.FanOutWarning.SplitLine"),
            2, 0.5, 0.25, FanOutLevelCalculator.SplitLossDb(2)));
        vm.VerdictLines.ShouldBe(new[]
        {
            string.Format(Translate("LogicPanel.FanOutWarning.BranchStillOne"), "NAND.A", 0.125),
            string.Format(Translate("LogicPanel.FanOutWarning.BranchStillOne"), "NAND.B", 0.125),
        });
    }

    [Fact]
    public void FailingBranch_GetsTheWouldFailLine_PassingBranchKeepsTheStillOneLine()
    {
        var warning = TwoLoadWarning(
            driverDisplayName: "SRC.Y", isNetworkInputSignal: false,
            new FanOutBranchLevel("NAND.A", 0.125, true),
            new FanOutBranchLevel("INV.A", 0.375, false));

        var vm = new LogicFanOutWarningViewModel(warning);

        vm.VerdictLines.ShouldBe(new[]
        {
            string.Format(Translate("LogicPanel.FanOutWarning.BranchStillOne"), "NAND.A", 0.125),
            string.Format(Translate("LogicPanel.FanOutWarning.BranchWouldFail"), "INV.A", 0.375),
        });
        vm.VerdictLines[1].ShouldContain("INV.A");
        vm.VerdictLines[0].Contains("INV.A").ShouldBeFalse("only the failing branch's line names it");
    }

    [Fact]
    public void NetworkInputWarning_UsesTheInputSignalLine()
    {
        var warning = TwoLoadWarning(
            driverDisplayName: "A", isNetworkInputSignal: true,
            new FanOutBranchLevel("NAND1.A", 0.125, true),
            new FanOutBranchLevel("NAND2.A", 0.125, true));

        var vm = new LogicFanOutWarningViewModel(warning);

        vm.WarningText.ShouldBe(string.Format(Translate("LogicPanel.FanOutWarning.InputLine"), "A", 2));
    }

    /// <summary>A two-load warning over an ideal 1×2 split of a 0.5-power driver (3.01 dB).</summary>
    private static LogicFanOutWarning TwoLoadWarning(
        string driverDisplayName, bool isNetworkInputSignal, params FanOutBranchLevel[] branches) =>
        new(
            driverDisplayName,
            isNetworkInputSignal,
            branches.Length,
            branches.Select(branch => branch.LoadName).ToArray(),
            new FanOutLevelReport(0.5, 0.25, FanOutLevelCalculator.SplitLossDb(branches.Length), branches));

    private static string Translate(string key) => LocalizationService.Instance.Translate(key);
}
