using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// End-to-end placement diagnostics for transformed GDS instances:
/// <see cref="GdsTestWriter"/> fixture → <see cref="GdsReader"/> →
/// <see cref="GdsHierarchyImporter"/> → <see cref="GdsPlacementPlan"/> →
/// <see cref="GdsPlacementExecutor"/> onto a real headless canvas.
///
/// The leaf cell is deliberately asymmetric: bbox (2,3)–(12,7) µm in GDS
/// coordinates (NOT at the origin) with three labeled pins off the centerlines,
/// so any axis mix-up, origin mistake, or missing mirror shows up in the numbers.
/// Every expected value below is computed BY HAND from the GDS transform
/// (reflection first, then counter-clockwise rotation in the Y-up plane, then
/// translation), not derived from the production code:
/// app space is µm, Y-down, origin at the top-cell bbox top-left.
///
/// Fixture geometry (GDS, Y-up, µm): top bbox is (2,−7)–(97,12), so
/// appX = gdsX − 2 and appY = 12 − gdsY.
/// </summary>
public class GdsReflectedInstancePlacementTests
{
    private const double Tolerance = 1e-6;

    // ── Transform battery: identity / rot90 / rot180 / reflected / reflected+rot90 ──

    [Fact]
    public async Task PlacedInstances_MatchTrueGdsTransforms_ForAllCardinalAndReflectedCases()
    {
        var import = await ImportTransformBatteryAsync();
        var (canvas, executor) = CreateExecutor(TemplatesFromDrafts(import));

        var report = await executor.ExecuteAsync(PlanFrom(import));

        report.PlacedCount.ShouldBe(5);
        report.SkippedPlacements.ShouldBeEmpty();

        // Instance 1 — identity at GDS offset (0,0): true bbox (2,3)–(12,7) → app (0,5).
        // Pins: west (2,6)→(0,6) 180°, east (12,4)→(10,8) 0°, south (7,3)→(5,9) 90°.
        var identity = SingleComponentAt(canvas, 0, 5);
        identity.RotationDegrees.ShouldBe(0, Tolerance);
        AssertPin(identity, "west", 0, 6, 180);
        AssertPin(identity, "east", 10, 8, 0);
        AssertPin(identity, "south", 5, 9, 90);

        // Instance 2 — rot 90° CCW (GDS) at (20,0): T(x,y) = (−y+20, x);
        // bbox (13,2)–(17,12) → app top-left (11,0), app rotation 270°.
        // west (2,6)→(14,2)→app(12,10), dir (−1,0)→(0,−1)→90°;
        // east (12,4)→(16,12)→app(14,0), dir (1,0)→(0,1)→270°;
        // south (7,3)→(17,7)→app(15,5), dir (0,−1)→(1,0)→0°.
        var rot90 = SingleComponentAt(canvas, 11, 0);
        rot90.RotationDegrees.ShouldBe(270, Tolerance);
        AssertPin(rot90, "west", 12, 10, 90);
        AssertPin(rot90, "east", 14, 0, 270);
        AssertPin(rot90, "south", 15, 5, 0);

        // Instance 3 — rot 180° at (40,0): T(x,y) = (−x+40, −y);
        // bbox (28,−7)–(38,−3) → app top-left (26,15).
        // west (2,6)→(38,−6)→app(36,18) 0°; east (12,4)→(28,−4)→app(26,16) 180°;
        // south (7,3)→(33,−3)→app(31,15) 270°.
        var rot180 = SingleComponentAt(canvas, 26, 15);
        rot180.RotationDegrees.ShouldBe(180, Tolerance);
        AssertPin(rot180, "west", 36, 18, 0);
        AssertPin(rot180, "east", 26, 16, 180);
        AssertPin(rot180, "south", 31, 15, 270);

        // Instance 4 — X-reflected (STRANS) at (60,0): T(x,y) = (x+60, −y);
        // bbox (62,−7)–(72,−3) → app top-left (60,15). The core model cannot
        // mirror, so the body is placed unreflected — but the pins must land on
        // the TRUE reflected positions, or the reconstructed connections anchor
        // at points where no pin is:
        // west (2,6)→(62,−6)→app(60,18) 180°; east (12,4)→(72,−4)→app(70,16) 0°;
        // south (7,3)→(67,−3)→app(65,15), dir (0,−1)→(0,1)→270°.
        var reflected = SingleComponentAt(canvas, 60, 15);
        reflected.RotationDegrees.ShouldBe(0, Tolerance);
        AssertPin(reflected, "west", 60, 18, 180);
        AssertPin(reflected, "east", 70, 16, 0);
        AssertPin(reflected, "south", 65, 15, 270);

        // Instance 5 — X-reflected + rot 90° at (90,0): T(x,y) = (y+90, x);
        // bbox (93,2)–(97,12) → app top-left (91,0), app rotation 270°.
        // west (2,6)→(96,2)→app(94,10) 90°; east (12,4)→(94,12)→app(92,0) 270°;
        // south (7,3)→(93,7)→app(91,5), dir (0,−1)→(−1,0)→180°.
        var reflectedRot90 = SingleComponentAt(canvas, 91, 0);
        reflectedRot90.RotationDegrees.ShouldBe(270, Tolerance);
        AssertPin(reflectedRot90, "west", 94, 10, 90);
        AssertPin(reflectedRot90, "east", 92, 0, 270);
        AssertPin(reflectedRot90, "south", 91, 5, 180);
    }

    [Fact]
    public async Task ReflectedInstances_CarryMirrorWarning_ButPinsAreExact()
    {
        var import = await ImportTransformBatteryAsync();
        var (canvas, executor) = CreateExecutor(TemplatesFromDrafts(import));

        var report = await executor.ExecuteAsync(PlanFrom(import));

        // The two STRANS instances are flagged in the plan and the report…
        report.Warnings.Count(w => w.Contains("LEAF#3") || w.Contains("LEAF#4")).ShouldBe(2);
        // …and no connection was reconstructed (instances are far apart), so the
        // canvas must not invent any either.
        report.ConnectedCount.ShouldBe(0);
        canvas.Components.ShouldHaveSingleItem("all five instances group into one component group");
    }

    // ── Nesting: top → mid → leaf ────────────────────────────────────────────

    [Fact]
    public async Task ThreeLevelHierarchy_LeafAbsorbedIntoMidDraft_OffsetsAccumulateExactly()
    {
        // Explode mode places only the top cell's DIRECT children; the leaf is
        // absorbed into the mid draft (flattened outlines + pins). MID references
        // LEAF at GDS (5,2), so the leaf bbox lands at (7,5)–(17,9) in mid space;
        // MID's own label "mw" sits at (7,8) — exactly where the leaf's "west"
        // port ((2,6) shifted by (5,2)) ends up, so the draft pin checks the full
        // two-level offset accumulation. TOP places MID identity at (30,10) and
        // rot90 at (60,10): top bbox (37,15)–(55,27) → appX = gdsX − 37,
        // appY = 27 − gdsY.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("MID", 30000, 10000)
                .SRef("MID", 60000, 10000, angleDegrees: 90)
            .EndCell()
            .BeginCell("MID")
                .SRef("LEAF", 5000, 2000)
                .Text(1, 10, "mw", 7000, 8000)
            .EndCell()
            .LeafCell()
            .EndLibrary()
            .ToArray());

        var import = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { PinDetection = LabelsOnlyDetection() });

        // Only MID becomes a draft/instance; LEAF never appears at top level.
        import.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("MID");
        import.Instances.Count.ShouldBe(2);
        import.Instances.ShouldAllBe(i => i.CellDraftName == "MID");

        // MID draft: bbox (7,5)–(17,9) → 10×4; the "mw" pin in draft-local app
        // space: (7−7, 9−8) = (0,1), nearest edge = left → 180°.
        var midDraft = import.ImportedCellDrafts[0];
        midDraft.WidthUm.ShouldBe(10, Tolerance);
        midDraft.HeightUm.ShouldBe(4, Tolerance);
        var mw = midDraft.Pins.ShouldHaveSingleItem();
        mw.Name.ShouldBe("mw");
        mw.XUm.ShouldBe(0, Tolerance);
        mw.YUm.ShouldBe(1, Tolerance);
        mw.AngleDegrees.ShouldBe(180, Tolerance);

        // Instance positions (import level): identity → app (0,8);
        // rot90: T(x,y) = (−y+60, x+10), bbox (51,17)–(55,27) → app (14,0).
        import.Instances[0].PositionXUm.ShouldBe(0, Tolerance);
        import.Instances[0].PositionYUm.ShouldBe(8, Tolerance);
        import.Instances[1].PositionXUm.ShouldBe(14, Tolerance);
        import.Instances[1].PositionYUm.ShouldBe(0, Tolerance);
        import.Instances[1].RotationDegrees.ShouldBe(270, Tolerance);

        // Executor level: the placed pins must sit where the leaf port actually
        // is. Identity: label (7,8)→top (37,18)→app (0,9).
        // Rot90: (7,8)→(−8+60, 7+10) = (52,17)→app (15,10); direction 180°+270° → 90°.
        var (canvas, executor) = CreateExecutor(TemplatesFromDrafts(import));
        var report = await executor.ExecuteAsync(PlanFrom(import));

        report.PlacedCount.ShouldBe(2);
        var identity = SingleComponentAt(canvas, 0, 8);
        identity.RotationDegrees.ShouldBe(0, Tolerance);
        AssertPin(identity, "mw", 0, 9, 180);
        var rot90 = SingleComponentAt(canvas, 14, 0);
        rot90.RotationDegrees.ShouldBe(270, Tolerance);
        AssertPin(rot90, "mw", 15, 10, 90);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The 5-instance transform battery over <see cref="LeafCell"/>: identity,
    /// rot90, rot180 (all at distinct X offsets), plus STRANS-reflected and
    /// reflected+rot90 variants — the gdsfactory mirror shapes.
    /// </summary>
    private static async Task<GdsCircuitImport> ImportTransformBatteryAsync()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("LEAF", 0, 0)
                .SRef("LEAF", 20000, 0, angleDegrees: 90)
                .SRef("LEAF", 40000, 0, angleDegrees: 180)
                .SRef("LEAF", 60000, 0, reflected: true)
                .SRef("LEAF", 90000, 0, angleDegrees: 90, reflected: true)
            .EndCell()
            .LeafCell()
            .EndLibrary()
            .ToArray());

        return await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { PinDetection = LabelsOnlyDetection() });
    }

    /// <summary>
    /// Pin detection restricted to the label layer: the fixture's bbox-filling
    /// extent polygon would otherwise add edge-heuristic pins at the edge
    /// midpoints and pollute the per-pin assertions.
    /// </summary>
    private static GdsPinDetectionOptions LabelsOnlyDetection() =>
        new() { WaveguideLayers = [(99, 0)] };

    /// <summary>Builds the executor over a real headless canvas with the given templates.</summary>
    private static (DesignCanvasViewModel Canvas, GdsPlacementExecutor Executor) CreateExecutor(
        params ComponentTemplate[] templates)
    {
        var canvas = new DesignCanvasViewModel();
        return (canvas, new GdsPlacementExecutor(canvas, new CommandManager(), () => templates));
    }

    /// <summary>
    /// Registers every imported draft as a same-named template (the sanitized
    /// cell name, like <c>GdsCellDraftMapper</c> produces) carrying the draft's
    /// pins verbatim, then wraps the import in the outcome the plan consumes.
    /// </summary>
    private static ComponentTemplate[] TemplatesFromDrafts(GdsCircuitImport import) =>
        import.ImportedCellDrafts.Select(draft => new ComponentTemplate
        {
            Name = GdsCellDraftMapper.SanitizeComponentName(draft.CellName),
            Category = "Test",
            PdkSource = "importedpdk",
            WidthMicrometers = draft.WidthUm,
            HeightMicrometers = draft.HeightUm,
            PinDefinitions = draft.Pins
                .Select(p => new PinDefinition(p.Name, p.XUm, p.YUm, p.AngleDegrees))
                .ToArray(),
            CreateSMatrix = pins => new CAP_Core.LightCalculation.SMatrix(
                pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList(),
                new List<(Guid, double)>()),
        }).ToArray();

    private static GdsPlacementPlan PlanFrom(GdsCircuitImport import) =>
        GdsPlacementPlan.FromOutcome(new GdsImportOutcome
        {
            TopCellName = import.TopCellName,
            Mode = import.Mode,
            RegisteredComponents = import.ImportedCellDrafts
                .Select(d => new GdsRegisteredComponent(
                    d.CellName, GdsCellDraftMapper.SanitizeComponentName(d.CellName)))
                .ToList(),
            Instances = import.Instances,
            Connections = import.Connections,
            Warnings = import.Warnings,
            Infos = import.Infos,
            UserPdkName = "importedpdk",
        });

    /// <summary>The single component whose rotated bbox top-left sits at (x, y) — positions are unique per fixture.</summary>
    private static Component SingleComponentAt(DesignCanvasViewModel canvas, double x, double y)
    {
        var all = canvas.Components
            .SelectMany(vm => vm.Component is ComponentGroup group
                ? group.ChildComponents.AsEnumerable()
                : new[] { vm.Component })
            .ToList();
        return all
            .Where(c => Math.Abs(c.PhysicalX - x) < Tolerance && Math.Abs(c.PhysicalY - y) < Tolerance)
            .ShouldHaveSingleItem();
    }

    /// <summary>Asserts the pin's ABSOLUTE canvas position and world angle (the quantities routing/export consume).</summary>
    private static void AssertPin(
        Component component, string pinName, double expectedX, double expectedY, double expectedAngle)
    {
        var pin = component.PhysicalPins.Single(p => p.Name == pinName);
        var (x, y) = pin.GetAbsolutePosition();
        x.ShouldBe(expectedX, Tolerance, $"pin '{pinName}' absolute X");
        y.ShouldBe(expectedY, Tolerance, $"pin '{pinName}' absolute Y");
        pin.GetAbsoluteAngle().ShouldBe(expectedAngle, Tolerance, $"pin '{pinName}' absolute angle");
    }

    private static async Task<GdsLibrary> ReadLibraryAsync(byte[] gds) =>
        await new GdsReader().ReadAsync(new MemoryStream(gds));
}

/// <summary>GDS fixture cells shared by the reflected-placement diagnostics.</summary>
file static class GdsReflectedPlacementCells
{
    /// <summary>
    /// Asymmetric leaf cell: bbox (2,3)–(12,7) µm (extent rectangle on the
    /// non-waveguide layer (111,0), so it never fires the edge heuristic) with
    /// three off-centerline labels on (1,10): "west" at (2,6) → local app
    /// (0,1) 180°, "east" at (12,4) → (10,3) 0°, "south" at (7,3) → (5,4) 90°.
    /// </summary>
    public static GdsTestWriter LeafCell(this GdsTestWriter writer) =>
        writer
            .BeginCell("LEAF")
                .Boundary(111, 0, (2000, 3000), (12000, 3000), (12000, 7000), (2000, 7000), (2000, 3000))
                .Text(1, 10, "west", 2000, 6000)
                .Text(1, 10, "east", 12000, 4000)
                .Text(1, 10, "south", 7000, 3000)
            .EndCell();
}
