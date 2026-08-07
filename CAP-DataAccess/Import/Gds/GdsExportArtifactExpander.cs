namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Recognizes the auxiliary cells our OWN exporters write into a .gds
/// (<see cref="ArtifactCellNames"/>) and decides their explode-import fate.
/// The mixed-backend nazca partial is written UNFLATTENED — its device cells
/// are real SREFs carrying port labels — so the explode import recurses ONE
/// level into it (<see cref="TryExpandArtifactInstances"/>) and the devices
/// import like top-level instances. Only a truly flat (reference-less)
/// partial — or one nested deeper than the documented
/// top → optional wrapper → partial → devices shape — is skipped with one
/// info note, since flattened partial geometry cannot be reconstructed (v1).
/// </summary>
internal static class GdsExportArtifactExpander
{
    /// <summary>
    /// Cell names our OWN exporters write as auxiliary artifacts into a .gds.
    /// Currently the mixed-backend export's nazca partial
    /// (<c>MixedBackendGdsOrchestrator.NazcaPartialTopCellName</c> in CAP.Avalonia;
    /// duplicated here as a literal because CAP-DataAccess must not reference the
    /// UI assembly). Add future artifact names here. The match looks through
    /// pass-through wrappers (<see cref="TryUnwrapToArtifact"/>): gdsfactory's
    /// <c>import_gds</c> names a merged component after the source file's TOP
    /// cell, so a re-imported mixed-backend export nests the partial under
    /// nazca's default <c>nazca</c> wrapper — the top cell's direct child is the
    /// wrapper, not the artifact cell itself.
    /// </summary>
    public static readonly IReadOnlySet<string> ArtifactCellNames =
        new HashSet<string>(StringComparer.Ordinal) { "ConnectAPIC_NazcaPartial" };

    /// <summary>
    /// True when <paramref name="cellName"/> IS one of our export artifacts or
    /// reaches one through a chain of pure pass-through wrappers (no elements of
    /// its own beyond a single untransformed — 1×1, unmagnified, unreflected,
    /// unrotated — reference; the same unwrap rule the import dialog applies to
    /// top-cell candidates, cf. <c>GdsImportService.UnwrapPassThroughTopCell</c>).
    /// <paramref name="artifactLeaf"/> receives the matched artifact cell name —
    /// equal to <paramref name="cellName"/> for a direct hit.
    /// </summary>
    public static bool TryUnwrapToArtifact(
        GdsLibrary library, string cellName, out string artifactLeaf)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = cellName;
        while (visited.Add(current))
        {
            if (ArtifactCellNames.Contains(current))
            {
                artifactLeaf = current;
                return true;
            }
            if (!library.Cells.TryGetValue(current, out var cell)
                || cell.Elements.Count != 1
                || cell.Elements[0] is not GdsReference reference
                || reference.Columns != 1
                || reference.Rows != 1
                || reference.Reflected
                || Math.Abs(reference.Magnification - 1.0) > 1e-9
                || Math.Abs(reference.AngleDegrees
                            - (360.0 * Math.Round(reference.AngleDegrees / 360.0))) > 1e-9)
            {
                artifactLeaf = string.Empty;
                return false;
            }
            current = reference.CellName;
        }

        // Reference cycle before any artifact — not a pass-through chain.
        artifactLeaf = string.Empty;
        return false;
    }

    /// <summary>
    /// One level of recursion into an export-artifact cell: the partial's DIRECT
    /// child references (AREF members expanded), each composed through the
    /// artifact instance's own transform — and through the pass-through
    /// wrapper's reference when the artifact hides behind one — into top-cell
    /// space, so the children import exactly like top-level instances.
    /// <paramref name="artifactLeaf"/> is guaranteed defined:
    /// <see cref="TryUnwrapToArtifact"/> resolved it. Returns false when the
    /// documented shape does not apply — the artifact cell holds no references
    /// at all (a truly flat partial whose geometry cannot be reconstructed) or
    /// sits behind MORE than one wrapper level; the caller then keeps the
    /// skip + info note.
    /// </summary>
    public static bool TryExpandArtifactInstances(
        GdsLibrary library,
        GdsInstance instance,
        string artifactLeaf,
        out IReadOnlyList<GdsInstance> nested)
    {
        nested = [];
        var childRefs = library.Cells[artifactLeaf].Elements
            .OfType<GdsReference>()
            .ToList();
        if (childRefs.Count == 0)
            return false; // Truly flat partial — nothing to recurse into.

        // The transform chain from the artifact cell's space up into top-cell
        // space: the instance's own transform, preceded by the wrapper's single
        // pass-through reference when a wrapper sits between them. At most ONE
        // wrapper level is unrolled — anything deeper falls back to the skip.
        var chain = GdsInstancePinProjector.TrueTransform(instance);
        if (!string.Equals(instance.CellName, artifactLeaf, StringComparison.Ordinal))
        {
            // TryUnwrapToArtifact validated the wrapper: exactly one element, a
            // pass-through reference.
            var wrapperRef = (GdsReference)library.Cells[instance.CellName].Elements[0];
            if (!string.Equals(wrapperRef.CellName, artifactLeaf, StringComparison.Ordinal))
                return false; // Deeper nesting than top → wrapper → partial.
            chain = GdsTransform.FromReference(wrapperRef, 0, 0).Then(chain);
        }

        var expanded = new List<GdsInstance>();
        foreach (var childRef in childRefs)
        {
            for (int row = 0; row < childRef.Rows; row++)
            {
                for (int column = 0; column < childRef.Columns; column++)
                {
                    var composed = GdsTransform.FromReference(childRef, column, row).Then(chain);
                    expanded.Add(DecomposeToInstance(childRef.CellName, composed));
                }
            }
        }
        nested = expanded;
        return true;
    }

    /// <summary>
    /// Splits a composed transform back into <see cref="GdsInstance"/> fields:
    /// the translation, the rotation from the linear part's first column, the
    /// uniform scale as that column's length, and the reflection from the
    /// determinant's sign. Exact for the rotation/reflection/uniform-scale
    /// compositions GDS references produce (<see cref="GdsTransform.FromReference"/>).
    /// </summary>
    private static GdsInstance DecomposeToInstance(string cellName, GdsTransform transform)
    {
        double angle = Math.Atan2(transform.D, transform.A) * 180.0 / Math.PI;
        double magnification = Math.Sqrt(transform.A * transform.A + transform.D * transform.D);
        bool reflected = transform.A * transform.E - transform.B * transform.D < 0;
        return new GdsInstance
        {
            CellName = cellName,
            Offset = new GdsPoint(transform.C, transform.F),
            AngleDegrees = angle,
            Magnification = magnification,
            Reflected = reflected,
        };
    }
}
