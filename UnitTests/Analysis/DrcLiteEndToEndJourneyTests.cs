using CAP.Avalonia.ViewModels.Diagnostics;
using CAP_Core.Analysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Issue #915 rung-2 proof: the DRC-lite panel (<see cref="DesignValidationViewModel"/>)
/// catches a deliberately broken design end-to-end and passes the repaired one. The design
/// is built entirely through production machinery on the real bundled PDK processes
/// (<see cref="DrcLiteJourneyDesign"/>); the panel command runs headless, exactly as the
/// canvas "Design Checks" button invokes it.
/// </summary>
public class DrcLiteEndToEndJourneyTests
{
    /// <summary>
    /// The five journey steps: (1) build the broken design, (2) run the panel validation,
    /// (3) assert the complete deduplicated localized result set with attribution,
    /// (4) repair the design and assert zero findings, (5) assert the demo/playground
    /// process stays silent on PDK-dependent rules while PDK-agnostic rules still fire.
    /// </summary>
    [Fact]
    public void BrokenDesign_IsCaught_FixedDesign_Passes_Playground_StaysSilent()
    {
        // ── Journey step 1: build the deliberately broken design on real PDK processes ──
        var broken = DrcLiteJourneyDesign.BuildBroken();

        broken.DanglingPin.ShouldNotBeNull();
        broken.BendConnection.ShouldNotBeNull();
        broken.BendConnection.RoutedPath.ShouldNotBeNull();
        broken.BendConnection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeTrue(
            "the real router must degrade under the Cornerstone 30 µm floor — no test-side flag stuffing");

        // ── Journey step 2: run the panel command headless ──
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            broken.Connections,
            allComponents: broken.Components,
            pdkSourceByComponent: broken.PdkSourceByComponent,
            enabledPdkNames: broken.EnabledPdkNames,
            minWaveguideSpacingMicrometers: broken.MinWaveguideSpacingMicrometers);

        // ── Journey step 3: the complete result set — types, counts, attribution, text ──
        panel.HasIssues.ShouldBeTrue();
        panel.Issues.Count.ShouldBe(5, Describe(panel));

        // 3a: exactly one dangling pin, attributed to the Cornerstone coupler's o2 input.
        var dangling = panel.Issues.Where(i => i.Type == DesignIssueType.UnconnectedPin).ToList();
        dangling.Count.ShouldBe(1);
        dangling[0].Connection.ShouldBeNull();
        dangling[0].Description.ShouldContain(".o2");
        var (pinX, pinY) = broken.DanglingPin.GetAbsolutePosition();
        dangling[0].X.ShouldBe(pinX, 1e-9);
        dangling[0].Y.ShouldBe(pinY, 1e-9);

        // 3b: exactly two pin mismatches (width + layer) on the cross-PDK connection,
        // fired from PDK-stamped pin data (SiEPIC 0.5 µm / layer 1 vs Cornerstone 1.2 µm / layer 203).
        var mismatches = panel.Issues.Where(i => i.Type == DesignIssueType.PinMismatch).ToList();
        mismatches.Count.ShouldBe(2);
        mismatches.ShouldAllBe(i => ReferenceEquals(i.Connection, broken.MismatchConnection));
        var width = mismatches.Single(i => i.Description.Contains("width mismatch"));
        width.Description.ShouldContain("0.5");
        width.Description.ShouldContain("1.2");
        var layer = mismatches.Single(i => i.Description.Contains("layer mismatch"));
        layer.Description.ShouldContain("layer 1");
        layer.Description.ShouldContain("layer 203");

        // 3c: exactly one spacing violation on the styled parallel pair (1.0 µm edge-to-edge).
        var spacing = panel.Issues.Where(i => i.Type == DesignIssueType.WaveguideSpacingViolation).ToList();
        spacing.Count.ShouldBe(1);
        spacing[0].Description.ShouldContain("too close");
        new[] { broken.SpacingConnectionA, broken.SpacingConnectionB }
            .ShouldContain(spacing[0].Connection);

        // 3d: exactly one bend-radius finding, on the router-produced tight route.
        var bend = panel.Issues.Where(i => i.Type == DesignIssueType.BendRadiusBelowProcessMinimum).ToList();
        bend.Count.ShouldBe(1);
        bend[0].Connection.ShouldBe(broken.BendConnection);
        bend[0].Description.ShouldContain("below process minimum");

        // 3e: every message is human-readable (no empty text, no bare resource keys) and
        // no finding is duplicated.
        foreach (var issue in panel.Issues)
        {
            issue.Description.ShouldNotBeNullOrWhiteSpace();
            issue.Description.ShouldContain(" ");
        }
        panel.Issues
            .GroupBy(i => (i.Type, i.Connection, i.X, i.Y, i.Description))
            .ShouldAllBe(g => g.Count() == 1);

        // ── Journey step 4: repair the design and re-run — zero findings ──
        var fixedDesign = DrcLiteJourneyDesign.BuildFixed();
        var fixedPanel = new DesignValidationViewModel();
        fixedPanel.RunValidation(
            fixedDesign.Connections,
            allComponents: fixedDesign.Components,
            pdkSourceByComponent: fixedDesign.PdkSourceByComponent,
            enabledPdkNames: fixedDesign.EnabledPdkNames,
            minWaveguideSpacingMicrometers: fixedDesign.MinWaveguideSpacingMicrometers);

        fixedPanel.Issues.ShouldBeEmpty(Describe(fixedPanel));
        fixedPanel.HasIssues.ShouldBeFalse();
        fixedPanel.StatusText.ShouldBe("No issues found");

        // ── Journey step 5: demo/playground process — no optical cross-section declared ──
        var demo = DrcLiteJourneyDesign.BuildDemoPlayground();

        demo.Components.SelectMany(c => c.PhysicalPins).ShouldAllBe(
            p => p.WaveguideWidthMicrometers == null && p.Layer == null);

        var demoPanel = new DesignValidationViewModel();
        demoPanel.RunValidation(
            demo.Connections,
            allComponents: demo.Components,
            processLockActive: false);

        demoPanel.Issues.Count(i => i.Type == DesignIssueType.PinMismatch).ShouldBe(0);
        demoPanel.Issues.Count(i => i.Type == DesignIssueType.WaveguideSpacingViolation).ShouldBe(0);
        demoPanel.Issues.Count(i => i.Type == DesignIssueType.BendRadiusBelowProcessMinimum).ShouldBe(0);
        demoPanel.Issues.Count(i => i.Type == DesignIssueType.UnconnectedPin).ShouldBe(3);
        demoPanel.Issues.Count.ShouldBe(3, Describe(demoPanel));
    }

    private static string Describe(DesignValidationViewModel panel) =>
        string.Join(" | ", panel.Issues.Select(i => $"{i.Type}: {i.Description}"));
}
