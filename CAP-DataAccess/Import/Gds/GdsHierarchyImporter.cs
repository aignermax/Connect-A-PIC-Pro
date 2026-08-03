using System.Globalization;

namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Turns a parsed GDS library into a pure-data circuit description
/// (<see cref="GdsCircuitImport"/>): component drafts for unknown cells, placed
/// instances, and abutment connections reconstructed from pin positions. No
/// canvas, <c>Component</c> or UI objects are created — the service layer
/// consumes the result.
///
/// Two modes (see <see cref="GdsHierarchyImportOptions.Mode"/>):
/// <list type="bullet">
/// <item><b>ExplodeHierarchy</b>: the top cell's direct children become
/// instances. Cells resolved by
/// <see cref="GdsHierarchyImportOptions.ResolveKnownComponent"/> reference the
/// existing PDK component; unknown cells become drafts whose outlines/pins
/// absorb their whole subtree (one level of components, matching
/// <see cref="GdsCellFlattener.GetInstanceTree"/>).</item>
/// <item><b>BlackBox</b>: the whole top cell becomes a single draft.</item>
/// </list>
///
/// A draft's pins are the cell's OWN port labels (nested labels belong to
/// absorbed sub-cells) plus the edge heuristic over its fully flattened
/// geometry. The circuit's external ports, by contrast, are the top cell's own
/// port LABELS only (gdsfactory convention) — unlabeled geometry ends at the
/// layout boundary stay internal.
/// </summary>
public static class GdsHierarchyImporter
{
    /// <summary>
    /// Placeholder token inside <see cref="GdsCellDraft.RawCode"/> for the .gds
    /// file name. The service layer replaces it with the absolute path of the
    /// source .gds after copying it next to the user-PDK JSON (absolute because
    /// the raw-code executor runs the snippet from a temp file with an unrelated
    /// working directory, so a bare relative name would never resolve).
    /// </summary>
    public const string GdsFileNameToken = "{GdsFileName}";

    /// <summary>
    /// Imports <paramref name="topCellName"/> from <paramref name="library"/>.
    /// The work is CPU-bound and synchronous; the async signature matches the
    /// other import entry points and honors cancellation between stages.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The library contains no cells, the top cell is not defined, or the
    /// hierarchy is broken (undefined reference / cycle, from the flattener).
    /// </exception>
    public static Task<GdsCircuitImport> ImportAsync(
        GdsLibrary library,
        string topCellName,
        GdsHierarchyImportOptions? options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(topCellName);
        options ??= new GdsHierarchyImportOptions();
        options.Validate();

        if (library.Cells.Count == 0)
            throw new InvalidDataException("The GDS library contains no cells.");
        if (!library.Cells.ContainsKey(topCellName))
            throw new InvalidDataException($"GDS top cell '{topCellName}' is not defined in the library.");

        var session = new GdsHierarchyImportSession(library, topCellName, options);
        ct.ThrowIfCancellationRequested();
        var result = options.Mode == GdsHierarchyImportMode.BlackBox
            ? ImportBlackBox(session, topCellName, ct)
            : ImportExploded(session, topCellName, ct);
        return Task.FromResult(result);
    }

    // ── Black-box mode ───────────────────────────────────────────────────────

    private static GdsCircuitImport ImportBlackBox(
        GdsHierarchyImportSession session, string topCellName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var draft = session.BuildDraft(topCellName);
        WarnOnZeroSizeDraft(draft, session.Warnings);
        return new GdsCircuitImport
        {
            Mode = GdsHierarchyImportMode.BlackBox,
            TopCellName = topCellName,
            BoundingBox = session.TopBBox,
            ImportedCellDrafts = [draft],
            Warnings = session.Warnings,
        };
    }

    // ── Explode mode ─────────────────────────────────────────────────────────

    private static GdsCircuitImport ImportExploded(
        GdsHierarchyImportSession session, string topCellName, CancellationToken ct)
    {
        var gdsInstances = session.Flattener.GetInstanceTree(topCellName);
        var drafts = new List<GdsCellDraft>();
        var draftNames = new HashSet<string>();
        var placed = new List<GdsPlacedInstance>();
        var names = new List<string>();
        var pinsPerInstance = new List<IReadOnlyList<GdsAbsolutePin>>();
        var occurrences = new Dictionary<string, int>();
        // One entry per distinct reference transform (an AREF expands to one
        // instance per member — identical warnings must collapse into ONE per
        // reference, with the member count, instead of flooding one per member).
        var transformNotes = new Dictionary<TransformSignature, (string FirstInstance, int Count)>();

        foreach (var gdsInstance in gdsInstances)
        {
            ct.ThrowIfCancellationRequested();
            string cell = gdsInstance.CellName;
            var cellBBox = session.GetCellBBox(cell);
            var known = session.ResolveKnown(cell);

            if (known is null && draftNames.Add(cell))
            {
                var draft = session.BuildDraft(cell);
                WarnOnZeroSizeDraft(draft, session.Warnings);
                drafts.Add(draft);
            }
            else if (known is not null)
            {
                session.WarnOnSizeMismatchOnce(cell, known, cellBBox);
            }

            occurrences.TryGetValue(cell, out int occurrence);
            occurrences[cell] = occurrence + 1;
            string instanceName = $"{cell}#{occurrence}";

            double angle = GdsInstancePinProjector.Normalize360(gdsInstance.AngleDegrees);
            double snapped = SnapToCardinal(angle);
            bool nonCardinal = Math.Abs(GdsInstancePinProjector.Normalize180(angle - snapped)) > 1e-9;
            bool magnified = Math.Abs(gdsInstance.Magnification - 1.0) > 1e-9;
            if (nonCardinal || gdsInstance.Reflected || magnified || gdsInstance.Magnification < 0)
            {
                var signature = new TransformSignature(
                    cell, gdsInstance.AngleDegrees, gdsInstance.Reflected, gdsInstance.Magnification);
                transformNotes.TryGetValue(signature, out var note);
                transformNotes[signature] = (note.FirstInstance ?? instanceName, note.Count + 1);
            }

            var topLeft = GdsInstancePinProjector.ProjectPlacedBoundsTopLeft(gdsInstance, cellBBox, session.TopBBox);
            placed.Add(new GdsPlacedInstance
            {
                InstanceName = instanceName,
                CellName = cell,
                KnownComponentIdentifier = known?.Identifier,
                PdkSource = known?.PdkSource,
                CellDraftName = known is null ? cell : null,
                PositionXUm = topLeft.X,
                PositionYUm = topLeft.Y,
                RotationDegrees = GdsInstancePinProjector.Normalize360(-snapped),
                Reflected = gdsInstance.Reflected,
            });

            var cellPins = known?.Pins ?? session.GetCellPins(cell, cellBBox);
            pinsPerInstance.Add(
                GdsInstancePinProjector.ProjectPins(gdsInstance, cellBBox, cellPins, session.TopBBox));
            names.Add(instanceName);
        }

        WarnOnReferenceTransforms(session, transformNotes);

        if (gdsInstances.Count == 0)
        {
            session.Warnings.Add(
                $"Top cell '{topCellName}' contains no cell references — nothing to explode. " +
                "Use black-box mode to import it as a single component.");
        }
        WarnOnTopLevelGeometry(session, topCellName);

        ct.ThrowIfCancellationRequested();
        var topPorts = session.GetTopLevelPorts()
            .Select(p => new GdsAbsolutePin { Name = p.Name, XUm = p.XUm, YUm = p.YUm, AngleDegrees = p.AngleDegrees })
            .ToList();
        var connections = GdsAbutmentMatcher.Match(
            names, pinsPerInstance, topPorts, session.Options.AbutmentToleranceUm, session.Warnings);

        return new GdsCircuitImport
        {
            Mode = GdsHierarchyImportMode.ExplodeHierarchy,
            TopCellName = topCellName,
            BoundingBox = session.TopBBox,
            ImportedCellDrafts = drafts,
            Instances = placed,
            Connections = connections,
            Warnings = session.Warnings,
        };
    }

    private static double SnapToCardinal(double angleDegrees) =>
        GdsInstancePinProjector.Normalize360(
            90.0 * Math.Round(angleDegrees / 90.0, MidpointRounding.AwayFromZero));

    /// <summary>
    /// The transform properties that make an instance noteworthy for warnings.
    /// All expanded members of one AREF share the same signature, so keying the
    /// warnings on it collapses the per-member flood into one warning per
    /// reference (with member count).
    /// </summary>
    private readonly record struct TransformSignature(
        string Cell, double AngleDegrees, bool Reflected, double Magnification);

    /// <summary>
    /// Emits the rotation/reflection/magnification warnings once per distinct
    /// reference transform, including the member count when an array (or several
    /// identical references) expanded to more than one instance.
    /// </summary>
    private static void WarnOnReferenceTransforms(
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

            double snapped = SnapToCardinal(GdsInstancePinProjector.Normalize360(signature.AngleDegrees));
            if (Math.Abs(GdsInstancePinProjector.Normalize180(
                    GdsInstancePinProjector.Normalize360(signature.AngleDegrees) - snapped)) > 1e-9)
            {
                session.Warnings.Add(
                    $"{subject} {has} a non-cardinal rotation of " +
                    $"{Fmt(signature.AngleDegrees)}° — snapped to {Fmt(snapped)}° " +
                    "(gdsfactory layouts are Manhattan, so this is usually safe).");
            }
            if (signature.Reflected)
            {
                session.Warnings.Add(
                    $"{subject} {isAre} mirrored (GDS STRANS); placed " +
                    "unreflected (v1 limitation — the core component model has no mirror support). " +
                    "Pin positions for connection reconstruction use the true reflected transform.");
            }
            if (Math.Abs(signature.Magnification - 1.0) > 1e-9)
            {
                session.Warnings.Add(
                    $"{subject} {has} magnification " +
                    $"×{Fmt(signature.Magnification)}; placed at 1:1 scale (v1 limitation). " +
                    "Pin positions for connection reconstruction use the true magnified transform.");
            }
            if (signature.Magnification < 0)
            {
                session.Warnings.Add(
                    $"{subject} {has} a NEGATIVE magnification (×{Fmt(signature.Magnification)}) — a " +
                    "negative MAG implies an additional mirror the placement snap does not model, " +
                    "so the placed rotation can be off by 180°.");
            }
        }
    }

    /// <summary>
    /// Warns on zero-size drafts (unpersistable geometry). A PINLESS draft
    /// deliberately gets no warning here: the service layer reports the more
    /// actionable "not registered: no pins" message, and warning in both places
    /// would double-report the same fact.
    /// </summary>
    private static void WarnOnZeroSizeDraft(GdsCellDraft draft, List<string> warnings)
    {
        if (draft.WidthUm <= 0 || draft.HeightUm <= 0)
        {
            warnings.Add($"Cell '{draft.CellName}' has an empty bounding box; the draft has zero size.");
        }
    }

    private static void WarnOnTopLevelGeometry(GdsHierarchyImportSession session, string topCellName)
    {
        int own = session.Library.Cells[topCellName].Elements
            .Count(e => e is GdsPolygon or GdsPath);
        if (own > 0)
        {
            session.Warnings.Add(
                $"Top cell '{topCellName}' contains {own} polygon(s)/path(s) of its own (routing " +
                "geometry); only cell references become components — own geometry is not reconstructed (v1).");
        }
    }

    internal static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);
}
