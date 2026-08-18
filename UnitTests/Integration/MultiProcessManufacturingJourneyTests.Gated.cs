using CAP_Core.Components.Core;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Components;
using UnitTests.Export.CornerstoneDrc;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Tool-gated stations of the multi-process manufacturing journey (issue #1010) — the
/// executed GDS export (step 6) and the vendored CORNERSTONE pre-DRC deck over it
/// (step 7). Both gate like the existing round-trip suites (#995): CI provides nazca +
/// KLayout and runs them, local suites skip them cleanly. The fixture executes the
/// export once when a nazca-capable Python exists; both steps skip from the same
/// fixture state.
/// </summary>
public partial class MultiProcessManufacturingJourneyTests
{
    private const double GeometryToleranceUm = 0.05;
    private const double PositionToleranceUm = 0.75;

    [Trait("Category", "Slow")]
    [SkippableFact]
    public async Task Step6_ExecutedGdsExport_RoutesCarryTheirProcessGeometryAtCoordinates()
    {
        Skip.If(_journey.NazcaPython == null,
            "Step 6: no Python with nazca available — the executed export needs the real engine (CI provides it).");
        _journey.ExportedGdsPath.ShouldNotBeNull(
            $"Step 6: the nazca export of the journey design must succeed.\n{_journey.ExportLog}");

        GdsLibrary library;
        await using (var stream = File.OpenRead(_journey.ExportedGdsPath!))
            library = await new GdsReader().ReadAsync(stream);
        library.TopCellCandidates.ShouldContain("ConnectAPIC_Design",
            "Step 6: the exported GDS carries the design as its top cell");
        var designCell = library.Cells["ConnectAPIC_Design"];
        var wires = WireGeometryCandidates(designCell).ToList();

        // Chiplet A's coupler→MMI wire: Cornerstone xs_nc — 1.2 µm wide on NITRIDE (203).
        var (aFrom, aTo) = InternalWireEndpoints(_journey.Design.ChipletA, "cs_coupler", "o3", "cs_mmi", "o1");
        AssertWireGeometry(wires, aFrom, aTo,
            MultiProcessChipletJourneyDesign.CornerstoneGdsLayer, 1.2,
            "Step 6: chiplet A's route must keep the Cornerstone width/layer in the real GDS (#960)");

        // Chiplet B's Y-branch→taper wire: SiEPIC strip — 0.5 µm wide on WG (1).
        var (bFrom, bTo) = InternalWireEndpoints(_journey.Design.ChipletB, "si_ybranch", "port 2", "si_taper", "port 1");
        AssertWireGeometry(wires, bFrom, bTo,
            MultiProcessChipletJourneyDesign.SiepicGdsLayer, 0.5,
            "Step 6: chiplet B's route must keep the SiEPIC width/layer in the real GDS (#960)");
    }

    [Trait("Category", "Slow")]
    [SkippableFact]
    public async Task Step7_CornerstonePreDrc_ExportedGdsPinnedCleanForTheSinChiplet()
    {
        Skip.If(_journey.ExportedGdsPath == null,
            "Step 7: no executed export (no nazca Python) — the foundry-deck proof needs the real engine (CI provides it).");
        var klayout = await ExternalToolProbes.FindKlayoutAsync();
        Skip.If(klayout == null,
            "Step 7: no KLayout on PATH/$KLAYOUT — the foundry-deck proof needs the real engine (CI provides it).");

        // The vendored deck inspects exactly the SiN chiplet's fabrication layers
        // (GDS 203/204/…); the SiEPIC chiplet's layers are outside its scope, so this
        // run IS the SiN chiplet's pre-DRC. Pinned clean (#932 infrastructure, #995 pattern).
        var reportPath = Path.Combine(_journey.WorkDirectory, "multiprocess_journey.lyrdb");
        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            _journey.NazcaPython!, CornerstoneDrcPaths.RunnerScript, _journey.ExportedGdsPath!,
            "--klayout", klayout, "--report", reportPath);

        exitCode.ShouldBe(0,
            $"Step 7: the vendored foundry deck must complete.\nstdout:\n{output}\nstderr:\n{error}");
        output.ShouldContain("PASSED: 0 DRC violations.", Case.Sensitive,
            "Step 7: the SiN chiplet's exported geometry must be foundry-clean");
    }

    /// <summary>Absolute endpoint positions of one frozen intra-chiplet wire, by child identifiers.</summary>
    private static ((double X, double Y) From, (double X, double Y) To) InternalWireEndpoints(
        ComponentGroup chiplet, string fromComponent, string fromPin, string toComponent, string toPin)
    {
        PhysicalPin PinOf(string componentId, string pinName) =>
            chiplet.ChildComponents.Single(c => c.Identifier == componentId)
                .PhysicalPins.Single(p => p.Name == pinName);
        return (PinOf(fromComponent, fromPin).GetAbsolutePosition(),
                PinOf(toComponent, toPin).GetAbsolutePosition());
    }

    /// <summary>
    /// All waveguide candidates of the top cell: paths contribute their centerline
    /// (expanded by their width), polygons their outline — both as (layer, bbox).
    /// </summary>
    private static IEnumerable<(int Layer, double MinX, double MinY, double MaxX, double MaxY)> WireGeometryCandidates(
        GdsCell designCell)
    {
        foreach (var polygon in designCell.Elements.OfType<GdsPolygon>())
        {
            yield return (polygon.Layer,
                polygon.Points.Min(p => p.X), polygon.Points.Min(p => p.Y),
                polygon.Points.Max(p => p.X), polygon.Points.Max(p => p.Y));
        }
        foreach (var path in designCell.Elements.OfType<GdsPath>())
        {
            var half = path.WidthMicrometers / 2;
            yield return (path.Layer,
                path.Points.Min(p => p.X) - half, path.Points.Min(p => p.Y) - half,
                path.Points.Max(p => p.X) + half, path.Points.Max(p => p.Y) + half);
        }
    }

    /// <summary>
    /// Asserts a wire on the expected layer whose bounding box matches the editor
    /// segment — Y-flipped into the export's coordinate system (nazcaY = -editorY) —
    /// widened to the process cross-section's width.
    /// </summary>
    private static void AssertWireGeometry(
        List<(int Layer, double MinX, double MinY, double MaxX, double MaxY)> wires,
        (double X, double Y) from, (double X, double Y) to,
        int expectedLayer, double expectedWidthUm, string because)
    {
        // A straight waveguide widens perpendicular to its direction only (flush ends).
        var horizontal = Math.Abs(to.X - from.X) >= Math.Abs(to.Y - from.Y);
        var half = expectedWidthUm / 2;
        var expectedMinX = Math.Min(from.X, to.X) - (horizontal ? 0 : half);
        var expectedMaxX = Math.Max(from.X, to.X) + (horizontal ? 0 : half);
        var expectedMinY = -Math.Max(from.Y, to.Y) - (horizontal ? half : 0);
        var expectedMaxY = -Math.Min(from.Y, to.Y) + (horizontal ? half : 0);

        wires.ShouldContain(
            wire => wire.Layer == expectedLayer
                && Math.Abs((wire.MaxX - wire.MinX) - (expectedMaxX - expectedMinX)) < GeometryToleranceUm
                && Math.Abs((wire.MaxY - wire.MinY) - (expectedMaxY - expectedMinY)) < GeometryToleranceUm
                && Math.Abs((wire.MinX + wire.MaxX) / 2 - (expectedMinX + expectedMaxX) / 2) < PositionToleranceUm
                && Math.Abs((wire.MinY + wire.MaxY) / 2 - (expectedMinY + expectedMaxY) / 2) < PositionToleranceUm,
            $"{because}: no geometry on layer {expectedLayer} matches the wire " +
            $"({from.X:F2},{from.Y:F2}) → ({to.X:F2},{to.Y:F2}) at {expectedWidthUm} µm width. " +
            $"Found: {string.Join("; ", wires.Select(w => $"L{w.Layer} [{w.MinX:F2}..{w.MaxX:F2}]×[{w.MinY:F2}..{w.MaxY:F2}]"))}");
    }
}
