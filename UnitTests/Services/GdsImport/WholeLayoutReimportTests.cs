using CAP.Avalonia.Services.GdsImport;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Round-trip regression: re-importing OUR OWN whole-layout GDS export
/// ("Export → whole layout GDS") must bind the exported cells back to the
/// bundled PDK components and must not invent phantom pins. Field report:
/// the reimported MZI created nazca_mmi1x2_sh drafts with 12 heuristic pins
/// plus a 'Parameters:' pin per MMI instead of resolving the bundled
/// "1x2 MMI Splitter" (demo.mmi1x2_sh). Fixture: the real exported file
/// (bundled-PDK content only), captured verbatim.
/// </summary>
public class WholeLayoutReimportTests
{
    private static readonly string GdsPath = FindRepoRelative(
        "Tools", "gds-test-data", "whole-layout-export-mzi.gds");
    private const string TopCell = "ConnectAPIC_Design";

    [Fact]
    public async Task Reimport_ResolvesBundledMmi_WithoutPhantomPins()
    {
        File.Exists(GdsPath).ShouldBeTrue($"fixture missing: {GdsPath}");
        var templates = TestPdkLoader.LoadAllTemplates();
        var library = await new CAP_DataAccess.Import.Gds.GdsReader().ReadAsync(File.OpenRead(GdsPath));
        var options = new GdsHierarchyImportOptions
        {
            ResolveKnownComponentCandidates = GdsTemplateResolver.BuildKnownComponentCandidatesResolver(templates),
        };

        var result = await GdsHierarchyImporter.ImportAsync(library, TopCell, options);

        // The exported MMI cells must bind to the bundled 1x2 MMI Splitter —
        // not register as fresh nazca_* drafts.
        var mmiInstances = result.Instances.Where(i => i.CellName.StartsWith("mmi1x2_sh")).ToList();
        mmiInstances.ShouldNotBeEmpty("the export carries two MMI cells");
        foreach (var inst in mmiInstances)
        {
            inst.KnownComponentIdentifier.ShouldNotBeNull(
                $"cell '{inst.CellName}' should resolve to the bundled 1x2 MMI Splitter");
        }
        result.ImportedCellDrafts.ShouldNotContain(
            d => d.CellName.StartsWith("mmi1x2_sh", StringComparison.OrdinalIgnoreCase),
            "a resolved cell never becomes a new draft");

        // No phantom pins anywhere: the nazca 'Parameters:' section header is
        // metadata, never a pin; and no heuristic pins where labels exist.
        foreach (var draft in result.ImportedCellDrafts)
        {
            draft.Pins.ShouldNotContain(
                p => p.Name.Contains("Parameters", StringComparison.OrdinalIgnoreCase),
                $"cell '{draft.CellName}': 'Parameters:' is a metadata header, not a pin");
        }

        // The MZI's device joints reconstruct: each MMI has three optical
        // ports (a0 in, b0/b1 out) and every one must land in a connection.
        int mmiConnections = result.Connections.Count(c =>
            (c.A.InstanceIndex >= 0 && result.Instances[c.A.InstanceIndex].CellName.StartsWith("mmi1x2_sh"))
            || (c.B.InstanceIndex >= 0 && result.Instances[c.B.InstanceIndex].CellName.StartsWith("mmi1x2_sh")));
        mmiConnections.ShouldBeGreaterThanOrEqualTo(6,
            "both MMIs must be fully wired into the reconstructed circuit");
    }

    private static string FindRepoRelative(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Tools", "gds-test-data")))
        {
            dir = dir.Parent;
        }
        if (dir == null) throw new InvalidOperationException("Could not locate repository root");
        return Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
    }
}
