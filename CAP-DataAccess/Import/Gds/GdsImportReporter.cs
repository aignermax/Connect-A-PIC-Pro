namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// User-presentable reporting for <see cref="GdsHierarchyImporter"/>: the
/// rotation/reflection/magnification warnings for placed instances and the
/// accounting of the top cell's OWN routing geometry (which polygons became
/// connections, which froze, which were dropped). Extracted from the importer
/// to keep both files under the architecture size limit; all methods append
/// to the session's <see cref="GdsHierarchyImportSession.Warnings"/> /
/// <see cref="GdsHierarchyImportSession.Infos"/>.
/// </summary>
internal static class GdsImportReporter
{
    /// <summary>
    /// The transform properties that make an instance noteworthy for warnings.
    /// All expanded members of one AREF share the same signature, so keying the
    /// warnings on it collapses the per-member flood into one warning per
    /// reference (with member count).
    /// </summary>
    internal readonly record struct TransformSignature(
        string Cell, double AngleDegrees, bool Reflected, double Magnification);

    /// <summary>
    /// Emits the rotation/reflection/magnification warnings once per distinct
    /// reference transform, including the member count when an array (or several
    /// identical references) expanded to more than one instance.
    /// </summary>
    public static void WarnOnReferenceTransforms(
        GdsHierarchyImportSession session,
        Dictionary<TransformSignature, (string FirstInstance, int Count)> transformNotes)
    {
        foreach (var (signature, note) in transformNotes)
        {
            var single = note.Count == 1;
            var subject = single
                ? $"Instance '{note.FirstInstance}' of cell '{signature.Cell}'"
                : $"{note.Count} instances of cell '{signature.Cell}' (first: '{note.FirstInstance}')";
            var has = single ? "has" : "have";
            var isAre = single ? "is" : "are";

            double snapped = GdsHierarchyImporter.SnapToCardinal(
                GdsInstancePinProjector.Normalize360(signature.AngleDegrees));
            if (Math.Abs(GdsInstancePinProjector.Normalize180(
                    GdsInstancePinProjector.Normalize360(signature.AngleDegrees) - snapped)) > 1e-9)
            {
                session.Warnings.Add(
                    $"{subject} {has} a non-cardinal rotation of " +
                    $"{GdsHierarchyImporter.Fmt(signature.AngleDegrees)}° — snapped to {GdsHierarchyImporter.Fmt(snapped)}° " +
                    "(gdsfactory layouts are Manhattan, so this is usually safe).");
            }
            if (signature.Reflected)
            {
                session.Warnings.Add(
                    $"{subject} {isAre} mirrored (GDS STRANS); the core component model has no " +
                    "mirror support, so the component body is placed unreflected (v1 limitation) — " +
                    "its pins are mirrored onto the true reflected positions, keeping the " +
                    "reconstructed connections exact.");
            }
            if (Math.Abs(signature.Magnification - 1.0) > 1e-9)
            {
                session.Warnings.Add(
                    $"{subject} {has} magnification " +
                    $"×{GdsHierarchyImporter.Fmt(signature.Magnification)}; placed at 1:1 scale (v1 limitation). " +
                    "Pin positions for connection reconstruction use the true magnified transform.");
            }
            if (signature.Magnification < 0)
            {
                session.Warnings.Add(
                    $"{subject} {has} a NEGATIVE magnification (×{GdsHierarchyImporter.Fmt(signature.Magnification)}) — a " +
                    "negative MAG implies an additional mirror the placement snap does not model, " +
                    "so the placed rotation can be off by 180°.");
            }
        }
    }

    /// <summary>
    /// Accounts for the top cell's OWN geometry (polygons/paths not belonging to
    /// any instance — typically routing our exporters flattened into the top
    /// cell). Waveguide-layer polygon networks bridging exactly two pins came
    /// back as real, re-routable connections (<paramref name="waveguideRoutes"/>),
    /// metal-layer networks as electrical connections (<paramref name="metalRoutes"/>);
    /// the remaining route/metal polygons ride the created group as frozen,
    /// non-re-routable paths (<paramref name="frozenPolygonCount"/>), and the
    /// polygons on all other layers ride it as render-only background geometry
    /// (<paramref name="backgroundPolygonCount"/>, see
    /// <see cref="GdsHierarchyImportSession.GetTopCellResidualPolygons"/>). All of
    /// that is normal, fully-reconstructed behavior → reported as INFO. Background
    /// polygons dropped to satisfy the outline-point cap are already warned about
    /// (with their true count) where they are collected — nothing else is dropped,
    /// so this reporter emits no warning of its own. Polygons contributed by
    /// dissolved route cells (<paramref name="dissolvedRoutePolygonCount"/>, see
    /// <see cref="GdsRouteCellDissolver"/>) count toward the routing geometry the
    /// report accounts for: they went through the same matcher as the top cell's
    /// own polygons.
    /// </summary>
    public static void ReportTopLevelGeometry(
        GdsHierarchyImportSession session,
        string topCellName,
        GdsRouteConnectivityResult waveguideRoutes,
        GdsRouteConnectivityResult metalRoutes,
        int frozenPolygonCount,
        int backgroundPolygonCount,
        int dissolvedRoutePolygonCount)
    {
        // Counted in outline-polygon units (a path expands to one quad per
        // segment) — the same units the route matcher and the frozen-path
        // collector work in; counting path ELEMENTS instead would let a
        // multi-segment path drive the remainder negative.
        int own = GdsPathOutliner.ExpandDrawnGeometry(session.Library.Cells[topCellName].Elements).Count()
            + dissolvedRoutePolygonCount;
        if (own == 0)
            return;

        int restoredPolygons =
            waveguideRoutes.ConsumedPolygonIndexes.Count + metalRoutes.ConsumedPolygonIndexes.Count;

        var restoredParts = new List<string>();
        if (waveguideRoutes.Pairs.Count > 0)
        {
            restoredParts.Add(
                $"{waveguideRoutes.ConsumedPolygonIndexes.Count} waveguide-layer polygon(s) were " +
                $"restored as {waveguideRoutes.Pairs.Count} real connection(s) (re-routable)");
        }
        if (metalRoutes.Pairs.Count > 0)
        {
            restoredParts.Add(
                $"{metalRoutes.ConsumedPolygonIndexes.Count} metal-layer polygon(s) were restored " +
                $"as {metalRoutes.Pairs.Count} electrical connection(s) (re-routable)");
        }
        if (frozenPolygonCount > 0)
        {
            restoredParts.Add(
                $"{frozenPolygonCount} route polygon(s) are imported as frozen paths (not re-routable)");
        }
        if (backgroundPolygonCount > 0)
        {
            restoredParts.Add(
                $"{backgroundPolygonCount} polygon(s) on other layers are imported as render-only " +
                "background geometry (not re-routable)");
        }

        if (restoredParts.Count > 0)
        {
            string source = dissolvedRoutePolygonCount > 0
                ? "of its own or from dissolved route cells"
                : "of its own";
            session.Infos.Add(
                $"Top cell '{topCellName}' contains {own} polygon(s)/path(s) {source} (routing " +
                $"geometry): {string.Join("; ", restoredParts)}.");
        }
    }

    /// <summary>
    /// Warns on zero-size drafts (unpersistable geometry). Only drafts degenerate
    /// in ONE dimension reach this in explode mode — cells empty in BOTH
    /// dimensions are dropped earlier with a single info note, draft and all. A
    /// PINLESS draft deliberately gets no warning here: the service layer reports
    /// the more actionable "not registered: no pins" message, and warning in both
    /// places would double-report the same fact.
    /// </summary>
    public static void WarnOnZeroSizeDraft(GdsCellDraft draft, List<string> warnings)
    {
        if (draft.WidthUm <= 0 || draft.HeightUm <= 0)
        {
            warnings.Add($"Cell '{draft.CellName}' has an empty bounding box; the draft has zero size.");
        }
    }
}
