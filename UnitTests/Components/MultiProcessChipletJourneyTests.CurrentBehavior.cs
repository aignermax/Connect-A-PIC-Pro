using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Green "today" pins for the documented-red stations of the multi-process journey:
/// each test proves the current single-process behavior the red step 5 (#937)
/// describes, so a future per-chiplet fix turns the pin red as a tripwire.
/// See <see cref="MultiProcessChipletJourneyTests"/> for the full journey. The step-3
/// pin now guards the canvas-level half of the shipped per-chiplet scope (#935):
/// ungrouped foreign content stays rejected even though chiplets may carry a second
/// process. The step-8 pin flipped with the per-process GDS export (#939) and now
/// guards that each chiplet's routes carry their own cross-section.
/// </summary>
public partial class MultiProcessChipletJourneyTests
{
    /// <summary>SiEPIC strip minimum bend radius (µm) from the bundled PDK.</summary>
    private const double SiepicMinBendRadiusUm = 5.0;

    [Fact]
    public void Placement_ProcessLockedCanvas_RejectsUngroupedForeignComponent()
    {
        // Companion to green step 3 (#935): at the raw canvas scope the lock still
        // rejects the second chiplet's process — only chiplets (groups bound to their
        // own process) cross the boundary; loose components and Playground behave as
        // before.
        var (cornerstone, siepic, catalog) = LoadProcessCatalog();
        var cornerstoneLock = ActiveProcessSelection.ForGroup(
            catalog.Single(g => g.MemberPdkNames.Contains(cornerstone.Name)));

        SingleProcessPolicy.CheckPlacement(cornerstoneLock, cornerstone.Name)
            .IsAllowed.ShouldBeTrue("chiplet A's own process must stay placeable");

        var (allowed, blockReason) = SingleProcessPolicy.CheckPlacement(cornerstoneLock, siepic.Name);
        allowed.ShouldBeFalse("ungrouped foreign content stays rejected at canvas scope (#935)");
        blockReason.ShouldNotBeNull();
        blockReason!.ShouldContain("monolithic");

        SingleProcessPolicy.CheckPlacement(ActiveProcessSelection.Playground(), cornerstone.Name)
            .IsAllowed.ShouldBeTrue();
        SingleProcessPolicy.CheckPlacement(ActiveProcessSelection.Playground(), siepic.Name)
            .IsAllowed.ShouldBeTrue("Playground still admits both chiplets without any checks");
    }

    [Fact]
    public void BendRadius_LockedProcess_ResolvesEachProcessMinimum_Today()
    {
        // Companion to red step 5 (#937): per-process limits exist and are honored —
        // but only while the whole design is locked to that one process.
        var (cornerstone, siepic, catalog) = LoadProcessCatalog();
        var pdks = new List<PdkDraft> { cornerstone, siepic };

        WaveguideBendRadiusResolver.Resolve(
                ActiveProcessSelection.ForGroup(
                    catalog.Single(g => g.MemberPdkNames.Contains(cornerstone.Name))), pdks)
            .ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
                "a Cornerstone-locked design resolves xs_nc's 30 µm minimum");
        WaveguideBendRadiusResolver.Resolve(
                ActiveProcessSelection.ForGroup(
                    catalog.Single(g => g.MemberPdkNames.Contains(siepic.Name))), pdks)
            .ShouldBe(SiepicMinBendRadiusUm,
                "a SiEPIC-locked design resolves the strip 5 µm minimum");
    }

    [Fact]
    public void BendRadius_Playground_OneFallbackForBothChiplets_Today()
    {
        // Companion to red step 5 (#937): Playground is the only mode that can hold both
        // chiplets — and there the resolver knows neither chiplet's limit; one global
        // fallback silently covers every route in the design.
        var (cornerstone, siepic, _) = LoadProcessCatalog();
        var pdks = new List<PdkDraft> { cornerstone, siepic };

        var resolved = WaveguideBendRadiusResolver.Resolve(ActiveProcessSelection.Playground(), pdks);

        resolved.ShouldBe(WaveguideBendRadiusResolver.FallbackMinimumMicrometers);
        resolved.ShouldNotBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm);
        resolved.ShouldNotBe(SiepicMinBendRadiusUm);
    }

    [Fact]
    public void GdsExport_EachChipletRoutesOnItsOwnProcessCrossSection()
    {
        // Flipped step-8 pin (#939): the gdsfactory main script sizes every route with
        // its own chiplet's cross-section — Cornerstone routes via 'xs_nc', SiEPIC
        // routes at the strip width stamped on its pins (SiEPIC declares no gdsfactory
        // routing cross-section) — never one majority-process cross-section for
        // everything. Assertions run on the generated export scripts (headless,
        // deterministic, no Python execution).
        var design = MultiProcessChipletJourneyDesign.BuildComposed();

        MixedBackendGdsOrchestrator.IsMixedBackendDesign(design.Canvas, design.Templates).ShouldBeTrue(
            "Cornerstone (gdsfactory-native) + SiEPIC (nazca-native) is a mixed-backend design");

        var scripts = new MixedBackendGdsOrchestrator().BuildScripts(
            design.Canvas,
            new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            null,
            design.Templates,
            Path.Combine(Path.GetTempPath(), "chiplet_journey", "main.py"));

        var routeSizings = RouteSizings(scripts.GdsFactoryScript);
        routeSizings.ShouldNotBeEmpty("the design carries routed waveguides the script must emit");
        routeSizings.ShouldContain("cross_section='xs_nc'",
            "chiplet A's wire (and the abutment, start-pin-owned) route on the Cornerstone cross-section");
        routeSizings.ShouldContain("width=0.50",
            "chiplet B's wire routes at the SiEPIC strip width stamped on its pins");
        routeSizings.ShouldNotContain("width=WG_WIDTH",
            "no routed waveguide falls back to the global default anymore");
        routeSizings.Distinct().Count().ShouldBeGreaterThan(1,
            "no single global cross-section may size every route in both chiplets (#939)");

        // The nazca partial renders SiEPIC placements only — no routed waveguide geometry.
        scripts.NazcaPartialScript.ShouldContain("ebeam_taper_te1550");
        scripts.NazcaPartialScript.ShouldNotContain("cross_section");
    }

    /// <summary>Loads both bundled PDKs and builds the production process catalog over them.</summary>
    private static (PdkDraft Cornerstone, PdkDraft Siepic, IReadOnlyList<ProcessGroup> Catalog)
        LoadProcessCatalog()
    {
        var cornerstone = MultiProcessChipletJourneyDesign.LoadPdk(MultiProcessChipletJourneyDesign.CornerstonePdkFile);
        var siepic = MultiProcessChipletJourneyDesign.LoadPdk(MultiProcessChipletJourneyDesign.SiepicPdkFile);
        var catalog = ProcessCatalog.BuildGroups(new[]
        {
            new PdkProcessEntry(cornerstone.Name, ProcessFingerprintFactory.From(cornerstone)),
            new PdkProcessEntry(siepic.Name, ProcessFingerprintFactory.From(siepic)),
        });
        return (cornerstone, siepic, catalog);
    }

    /// <summary>The sizing keyword argument of every routed straight the script emits.</summary>
    private static List<string> RouteSizings(string script) =>
        System.Text.RegularExpressions.Regex
            .Matches(script, @"gf\.components\.straight\(length=[\d.]+, ([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .ToList();
}
