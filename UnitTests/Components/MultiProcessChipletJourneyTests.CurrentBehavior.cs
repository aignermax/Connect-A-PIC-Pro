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
/// each test proves the current single-process behavior the red steps 3 (#935) and 8
/// (#939) describe, so a future per-chiplet fix turns the pin red as a tripwire.
/// (Step 5's pin fired with #937 and was replaced by the per-chiplet assertion below.)
/// See <see cref="MultiProcessChipletJourneyTests"/> for the full journey.
/// </summary>
public partial class MultiProcessChipletJourneyTests
{
    /// <summary>SiEPIC strip minimum bend radius (µm) from the bundled PDK.</summary>
    private const double SiepicMinBendRadiusUm = 5.0;

    [Fact]
    public void Placement_ProcessLockedCanvas_RejectsSecondChipletProcess_Today()
    {
        // Companion to red step 3 (#935): the canvas-global lock really rejects the
        // second chiplet's process today — only Playground admits both.
        var (cornerstone, siepic, catalog) = LoadProcessCatalog();
        var cornerstoneLock = ActiveProcessSelection.ForGroup(
            catalog.Single(g => g.MemberPdkNames.Contains(cornerstone.Name)));

        SingleProcessPolicy.CheckPlacement(cornerstoneLock, cornerstone.Name)
            .IsAllowed.ShouldBeTrue("chiplet A's own process must stay placeable");

        var (allowed, blockReason) = SingleProcessPolicy.CheckPlacement(cornerstoneLock, siepic.Name);
        allowed.ShouldBeFalse("today the design-global lock rejects chiplet B's process (#935)");
        blockReason.ShouldNotBeNull();
        blockReason!.ShouldContain("monolithic");

        SingleProcessPolicy.CheckPlacement(ActiveProcessSelection.Playground(), cornerstone.Name)
            .IsAllowed.ShouldBeTrue();
        SingleProcessPolicy.CheckPlacement(ActiveProcessSelection.Playground(), siepic.Name)
            .IsAllowed.ShouldBeTrue("Playground is the only mode that can hold both chiplets today");
    }

    [Fact]
    public void BendRadius_LockedProcess_ResolvesEachProcessMinimum_Today()
    {
        // Companion to step 5 (#937): the design-wide resolution still exists and
        // stays honored while the whole design is locked to one process — the
        // per-connection floor only takes over where endpoint PDKs differ.
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
    public void BendRadius_Playground_PerChipletFloors_FromEndpointPdks()
    {
        // Companion to green step 5 (#937, the fired tripwire): even in Playground —
        // the only mode that can hold both chiplets — the per-connection resolver
        // reads each endpoint PDK's own process minimum instead of one global value.
        var (cornerstone, siepic, _) = LoadProcessCatalog();
        var pdks = new List<PdkDraft> { cornerstone, siepic };
        const double canvasWideFallback = WaveguideBendRadiusResolver.FallbackMinimumMicrometers;

        WaveguideBendRadiusResolver.ResolveForEndpointPdks(
                cornerstone.Name, cornerstone.Name, pdks, canvasWideFallback)
            .ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
                "a Cornerstone↔Cornerstone pair floors at xs_nc's 30 µm");
        WaveguideBendRadiusResolver.ResolveForEndpointPdks(
                siepic.Name, siepic.Name, pdks, canvasWideFallback)
            .ShouldBe(SiepicMinBendRadiusUm,
                "a SiEPIC↔SiEPIC pair floors at the strip 5 µm — no longer over-constrained");
        WaveguideBendRadiusResolver.ResolveForEndpointPdks(
                cornerstone.Name, siepic.Name, pdks, canvasWideFallback)
            .ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
                "a cross-process pair takes the STRICTER floor, not the minimum over member PDKs");
        WaveguideBendRadiusResolver.ResolveForEndpointPdks(
                null, null, pdks, canvasWideFallback)
            .ShouldBe(canvasWideFallback,
                "endpoints without a resolvable PDK keep the canvas-wide fallback");
    }

    [Fact]
    public void GdsExport_AllRoutedWaveguidesShareOneMajorityCrossSection_Today()
    {
        // Companion to red step 8 (#939): today's export sizes every routed waveguide
        // with the single majority-process cross-section; the other chiplet's geometry
        // never appears on any route. Assertions run on the generated export scripts
        // (headless, deterministic, no Python/nazca execution).
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
        routeSizings.Distinct().Count().ShouldBe(1,
            "today one majority-process cross-section sizes every route in BOTH chiplets (#939)");
        routeSizings[0].ShouldBe("cross_section='xs_nc'",
            "the majority process (Cornerstone) owns the one global route cross-section");

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
