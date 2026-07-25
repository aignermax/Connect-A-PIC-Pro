using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using UnitTests.Integration;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Owner field report: many bundled components (couplers named) open "Edit Component" with an
/// EMPTY code view. The editor text is RawCode, else FoundryEditCodeSynthesis.For(...) — null
/// means a blank editor. These sweeps iterate EVERY component of EVERY bundled PDK and lock:
/// (a) non-empty editor code for everything except the four documented KLayout-only SiEPIC
/// cells, and (b) a renderable geometry-preview identity for every physical component.
/// </summary>
public class BundledPdkStructureAndPreviewTests
{
    // KLayout-only SiEPIC cells (no ubcpdk registry equivalent): deliberately no runnable editor
    // code — see FoundryEditCodeSynthesis; their canvas preview renders via the klayout route.
    private static readonly string[] KLayoutOnlySiepicCells =
    {
        "contra_directional_coupler",
        "ebeam_dc_halfring_straight",
        "ebeam_dc_te1550",
        "ebeam_taper_te1550",
    };

    // Analysis tools (ONA Analyzer) have no physical geometry and no runnable code by design.
    private const string AnalyzerSentinel = "__analyzer__";

    private static string PdkDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "CAP-DataAccess", "PDKs");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("CAP-DataAccess/PDKs not found above test binaries.");
    }

    private static List<PdkDraft> LoadAllBundledPdks(out List<string> pdkFiles)
    {
        pdkFiles = Directory.EnumerateFiles(PdkDirectory(), "*.json").OrderBy(f => f).ToList();
        var loader = new PdkLoader();
        return pdkFiles.Select(f => loader.LoadFromFileForEditing(f)).ToList();
    }

    private static bool IsAnalyzer(PdkComponentDraft comp) => comp.NazcaFunction == AnalyzerSentinel;

    [Fact]
    public void BundledPdks_EditorCode_NonEmptyForAllExceptDocumentedKLayoutOnlySiepicCells()
    {
        var pdks = LoadAllBundledPdks(out var pdkFiles);
        pdkFiles.Count.ShouldBeGreaterThanOrEqualTo(4, "demo/siepic/cornerstone/tools PDKs must be found");

        var emptyCodeFunctions = new SortedSet<string>(StringComparer.Ordinal);
        int checkedCount = 0;
        foreach (var pdk in pdks)
        {
            foreach (var comp in pdk.Components)
            {
                if (IsAnalyzer(comp))
                    continue;
                checkedCount++;

                // Production order in NewComponentViewModel.LoadForEdit: RawCode wins, else synthesis.
                if (!string.IsNullOrWhiteSpace(comp.RawCode))
                    continue;

                var synthesized = FoundryEditCodeSynthesis.For(
                    comp.GdsFactoryFunction, pdk.NazcaModuleName, comp.NazcaFunction, comp.NazcaParameters);
                if (synthesized is null)
                {
                    emptyCodeFunctions.Add(comp.NazcaFunction ?? "(none)");
                    continue;
                }
                synthesized.Value.Code.ShouldNotBeNullOrWhiteSpace(
                    $"'{pdk.Name}/{comp.Name}' must not open a blank code editor");
                synthesized.Value.Code.ShouldContain("component");
            }
        }

        checkedCount.ShouldBeGreaterThanOrEqualTo(67, "the sweep must really cover every bundled component");
        emptyCodeFunctions.ShouldBe(
            KLayoutOnlySiepicCells,
            ignoreOrder: true,
            customMessage: "only the four documented KLayout-only SiEPIC cells may open with an empty editor; " +
                           "any new entry here is a blank-editor regression the owner reported");
    }

    [Fact]
    public void BundledPdks_GeometryPreview_EveryPhysicalComponentHasRenderableIdentity()
    {
        var pdks = LoadAllBundledPdks(out _);
        int checkedCount = 0;
        foreach (var pdk in pdks)
        {
            foreach (var comp in pdk.Components)
            {
                if (IsAnalyzer(comp))
                    continue;
                checkedCount++;

                if (!string.IsNullOrWhiteSpace(comp.GdsFactoryFunction))
                {
                    // Bare (dotless) gdsfactory names get NO preview — RenderGdsFactoryAsync fails on them.
                    GdsFactoryPreviewCode.For(comp.GdsFactoryFunction).ShouldNotBeNull(
                        $"'{pdk.Name}/{comp.Name}' needs a module-qualified gdsFactoryFunction for a preview");
                }
                else
                {
                    comp.NazcaFunction.ShouldNotBeNullOrWhiteSpace(
                        $"'{pdk.Name}/{comp.Name}' has no geometry reference at all");
                    var key = new GdsPreviewKey(pdk.NazcaModuleName, comp.NazcaFunction, comp.NazcaParameters);
                    key.IsRenderable.ShouldBeTrue($"'{pdk.Name}/{comp.Name}' must be preview-renderable");
                }
            }
        }

        checkedCount.ShouldBeGreaterThanOrEqualTo(67, "the sweep must really cover every bundled component");
    }

    /// <summary>Opt-in real-environment lock: renders every component through the exact preview
    /// pipeline the app drives; runs only with LUNIMA_TEST_PYTHON3 set, skips per-component when a
    /// backend package is missing from that interpreter (env gap, not a Lunima bug).</summary>
    [Trait("Category", "Slow")]
    [Fact]
    public async Task BundledPdks_GeometryPreview_EveryPhysicalComponentRendersNonEmptyPolygons()
    {
        var (python, nazcaScript) = await GdsAlignmentTestSetup.ResolveEnvironmentAsync();
        var gdsFactoryScript = FindRepoFile(Path.Combine("scripts", "render_gdsfactory_preview.py"));
        if (python is null || nazcaScript is null || gdsFactoryScript is null)
            return; // env skip

        var nazca = new NazcaComponentPreviewService(python, nazcaScript);
        var gdsFactory = new GdsFactoryComponentPreviewService(python, gdsFactoryScript);

        var failures = new List<string>();
        int renderedCount = 0;
        foreach (var pdk in LoadAllBundledPdks(out _))
        {
            foreach (var comp in pdk.Components)
            {
                if (IsAnalyzer(comp))
                    continue;

                NazcaPreviewResult result;
                if (!string.IsNullOrWhiteSpace(comp.GdsFactoryFunction)
                    && GdsFactoryPreviewCode.For(comp.GdsFactoryFunction) is { } code)
                    result = await gdsFactory.RenderRawCodeAsync(code);
                else
                    result = await nazca.RenderAsync(pdk.NazcaModuleName, comp.NazcaFunction!, comp.NazcaParameters);

                if (!result.Success && IsEnvironmentGap(result.Error))
                    continue; // backend package missing in this interpreter — not a Lunima bug

                if (!result.Success)
                    failures.Add($"{pdk.Name}/{comp.Name}: render failed — {result.Error}");
                else if (result.Polygons.Count == 0)
                    failures.Add($"{pdk.Name}/{comp.Name}: rendered 0 polygons (empty geometry)");
                else
                    renderedCount++;
            }
        }

        failures.ShouldBeEmpty(
            $"every bundled component must render a non-empty preview ({renderedCount} rendered OK)." +
            $"\n{string.Join("\n", failures)}");
    }

    private static bool IsEnvironmentGap(string? error) =>
        error?.Contains("No module named") == true
        || error?.Contains("not installed") == true
        || error?.Contains("requires klayout") == true;

    private static string? FindRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }
}
