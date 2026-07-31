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
    /// file name. The UI layer replaces it with the bare file name after
    /// copying the source .gds next to the user-PDK JSON.
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
        WarnOnPinlessDraft(draft, session.Warnings);
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

        foreach (var gdsInstance in gdsInstances)
        {
            ct.ThrowIfCancellationRequested();
            string cell = gdsInstance.CellName;
            var cellBBox = session.GetCellBBox(cell);
            var known = session.ResolveKnown(cell);

            if (known is null && draftNames.Add(cell))
            {
                var draft = session.BuildDraft(cell);
                WarnOnPinlessDraft(draft, session.Warnings);
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
            if (Math.Abs(GdsInstancePinProjector.Normalize180(angle - snapped)) > 1e-9)
            {
                session.Warnings.Add(
                    $"Instance '{instanceName}' of cell '{cell}' has a non-cardinal rotation of " +
                    $"{Fmt(gdsInstance.AngleDegrees)}° — snapped to {Fmt(snapped)}° " +
                    "(gdsfactory layouts are Manhattan, so this is usually safe).");
            }
            if (gdsInstance.Reflected)
            {
                session.Warnings.Add(
                    $"Instance '{instanceName}' of cell '{cell}' is mirrored (GDS STRANS); placed " +
                    "unreflected (v1 limitation — the core component model has no mirror support). " +
                    "Pin positions for connection reconstruction use the true reflected transform.");
            }
            if (Math.Abs(gdsInstance.Magnification - 1.0) > 1e-9)
            {
                session.Warnings.Add(
                    $"Instance '{instanceName}' of cell '{cell}' has magnification " +
                    $"×{Fmt(gdsInstance.Magnification)}; placed at 1:1 scale (v1 limitation). " +
                    "Pin positions for connection reconstruction use the true magnified transform.");
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

    private static void WarnOnPinlessDraft(GdsCellDraft draft, List<string> warnings)
    {
        if (draft.Pins.Count == 0)
        {
            warnings.Add(
                $"Cell '{draft.CellName}': no pins detected — the component draft needs manual pin editing.");
        }
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
