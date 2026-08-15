using Shouldly;
using Xunit;

namespace UnitTests.Export.CornerstoneDrc;

/// <summary>
/// Pins the vendored CORNERSTONE SiN 300nm pre-DRC deck (<c>scripts/drc/cornerstone_sin300_drc.lydrc</c>):
/// it must exist, name its upstream source URL + pinned commit, stay batch-mode capable
/// (<c>source($input)</c>/<c>report(..., $report)</c> plumbing), and carry the foundry rule
/// values our DRC-lite limits were adopted from (250 nm gap/feature on the SiN core layer —
/// CORNERSTONE SiN 300nm design guidelines §5.4 Table 4). These tests need no KLayout and
/// run everywhere, so an accidental deck edit/swap fails CI immediately.
/// </summary>
public class CornerstoneDrcDeckProvenanceTests
{
    private const string PinnedCommit = "b57ee3a9f0809535b90525805f11eae089430ce2";

    [Fact]
    public void VendoredDeck_ExistsNextToTheRunnerScript()
    {
        File.Exists(CornerstoneDrcPaths.DeckFile).ShouldBeTrue(
            $"vendored deck missing at {CornerstoneDrcPaths.DeckFile}");
        File.Exists(CornerstoneDrcPaths.RunnerScript).ShouldBeTrue("runner script missing");
        File.Exists(Path.Combine(CornerstoneDrcPaths.DeckFolder, "README.md")).ShouldBeTrue(
            "provenance README missing");
    }

    [Fact]
    public void VendoredDeck_HeaderDocumentsSourceUrlCommitAndLicense()
    {
        var deck = File.ReadAllText(CornerstoneDrcPaths.DeckFile);
        deck.ShouldContain("https://github.com/cornerstone-uos/cornerstone-pdk");
        deck.ShouldContain(PinnedCommit);
        deck.ShouldContain("TAPR Open Hardware License");
    }

    [Fact]
    public void VendoredDeck_IsBatchModeCapable()
    {
        // Headless invocation contract: klayout -b -r deck.lydrc -rd input=... -rd report=...
        var deck = File.ReadAllText(CornerstoneDrcPaths.DeckFile);
        deck.ShouldContain("source($input)");
        deck.ShouldContain("report(\"DRC report\", $report)");
    }

    [Fact]
    public void VendoredDeck_EnforcesTheFoundryWaveguideLimits()
    {
        var deck = File.ReadAllText(CornerstoneDrcPaths.DeckFile);

        // SiN core (light field, GDS 203): 250 nm min feature + 250 nm min gap — the values
        // DRC-lite adopted (minWidthUm / minWaveguideSpacingUm = 0.25).
        deck.ShouldContain("sin_light_layer = input(203,0)");
        deck.ShouldContain("sin_light_layer.width(0.25");
        deck.ShouldContain("sin_light_layer.space(0.25");

        // SiN etch (dark field, GDS 204): 250 nm min feature + gap.
        deck.ShouldContain("sin_dark_layer = input(204, 0)");
        deck.ShouldContain("sin_dark_layer.width(0.25");
        deck.ShouldContain("sin_dark_layer.space(0.25");

        // 1 nm design grid, full-die area check on the outline layer (GDS 99).
        deck.ShouldContain("design_grid = 0.001");
        deck.ShouldContain("cell = input(99, 0)");
        deck.ShouldContain("cell.without_area(design_area)");
    }
}
