using System.Globalization;

namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The draft-construction half of <see cref="GdsHierarchyImportSession"/>:
/// builds <see cref="GdsCellDraft"/>s for explode mode and black-box mode
/// (pins, simplified outlines, raw-code snippet), split out to keep the
/// session file under the project's 500-line gate.
/// </summary>
internal sealed partial class GdsHierarchyImportSession
{
    public GdsCellDraft BuildDraft(string cellName)
    {
        var bbox = GetCellBBox(cellName);
        return new GdsCellDraft
        {
            CellName = cellName,
            WidthUm = bbox.Width,
            HeightUm = bbox.Height,
            Pins = GetCellPins(cellName, bbox),
            Outlines = BuildOutlines(cellName, bbox),
            RawCode = BuildRawCode(cellName),
        };
    }

    /// <summary>
    /// Builds the draft for BLACK-BOX mode: the whole top cell becomes one
    /// component, so its pins are the port labels of the ENTIRE flattened
    /// hierarchy (nested subcell labels become texts at their positions after
    /// flattening), not just the top cell's own labels — a whole-circuit black
    /// box has no own labels at all when nothing was explicitly exported as a
    /// circuit port. Subcell labels are prefixed with their instance context
    /// (<c>{cell}_{pin}</c>, or <c>{cell}#{occurrence}_{pin}</c> when the cell
    /// is placed more than once) so every pin name is unique and traceable;
    /// the top cell's own labels keep their bare names (they ARE the circuit's
    /// ports). The waveguide edge heuristic runs over the flattened geometry
    /// exactly like for explode-mode drafts. Pin kinds come from the detector's
    /// inference (<see cref="GdsPinDetector"/>): metal-touching or
    /// electrically-named labels become electrical, the rest stays kind-unknown
    /// (the optical default downstream).
    /// </summary>
    public GdsCellDraft BuildBlackBoxDraft(string cellName)
    {
        var bbox = GetCellBBox(cellName);
        return new GdsCellDraft
        {
            CellName = cellName,
            WidthUm = bbox.Width,
            HeightUm = bbox.Height,
            Pins = GetBlackBoxPins(cellName, bbox),
            Outlines = BuildOutlines(cellName, bbox),
            RawCode = BuildRawCode(cellName),
        };
    }

    /// <summary>
    /// Black-box pin detection: runs <see cref="GdsPinDetector"/> over the fully
    /// flattened top cell with every nested port label promoted to a
    /// context-prefixed text (see <see cref="BuildBlackBoxDraft"/>). Labels
    /// duplicated verbatim (same text, layer and anchor — e.g. demofab's
    /// doubled <c>c0</c> label on its eopm cell) collapse into ONE pin
    /// silently: two identical label records describe one physical pin, and
    /// keeping both would only trigger the duplicate-name rename warning.
    /// Coincident stacks of DIFFERENT labels (real pin label plus helper
    /// labels) collapse via <see cref="CollapseCoincidentLabels"/> before
    /// detection, exactly like explode-mode draft pins.
    /// </summary>
    private IReadOnlyList<DetectedPin> GetBlackBoxPins(string cellName, GdsBoundingBox bbox)
    {
        var flat = GetFlattened(cellName);
        var detectionCell = new FlattenedGdsCell { CellName = cellName };
        detectionCell.Polygons.AddRange(flat.Polygons);

        // How often each source cell occurs in the walk (decides the occurrence
        // qualifier in the prefix); derived from the text origins, so cells
        // whose instances carry no labels never disturb the numbering.
        var occurrencesPerCell = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var origin in flat.TextOrigins)
        {
            occurrencesPerCell[origin.CellName] =
                Math.Max(occurrencesPerCell.GetValueOrDefault(origin.CellName), origin.Occurrence + 1);
        }

        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        var labels = new List<GdsText>();
        for (int i = 0; i < flat.Texts.Count; i++)
        {
            var text = flat.Texts[i];
            if (!IsSingleLineLabel(text))
                continue;
            var origin = flat.TextOrigins[i];
            string label = origin.CellName == cellName
                ? text.Text
                : occurrencesPerCell.GetValueOrDefault(origin.CellName) > 1
                    ? $"{origin.CellName}#{origin.Occurrence}_{text.Text}"
                    : $"{origin.CellName}_{text.Text}";

            // 1 nm position quantization: the same label anchored twice at the
            // same spot is one pin, not a duplicate.
            string fingerprint = string.Create(
                CultureInfo.InvariantCulture,
                $"{label}|{text.Layer}|{text.TextType}|{Math.Round(text.Position.X * 1000)}|{Math.Round(text.Position.Y * 1000)}");
            if (!seenLabels.Add(fingerprint))
                continue;

            labels.Add(text with { Text = label });
        }

        detectionCell.Texts.AddRange(CollapseCoincidentLabels(labels, cellName));

        var pins = GdsPinNameNormalizer.Normalize(
            DetectWithAnyLayerFallback(detectionCell, bbox, cellName),
            $"Cell '{cellName}'",
            Warnings);
        return FilterExcludedGuessedPins(cellName, pins);
    }

    private IReadOnlyList<GdsOutlinePolygon> BuildOutlines(string cellName, GdsBoundingBox bbox)
    {
        var converted = GetFlattened(cellName).Polygons
            .Select(p => new GdsOutlinePolygon
            {
                Layer = p.Layer,
                DataType = p.DataType,
                Points = p.Points
                    .Select(gp => new GdsOutlinePoint(gp.X - bbox.MinX, bbox.MaxY - gp.Y))
                    .ToList(),
            })
            .ToList();

        var simplified = GdsOutlineSimplifier.Simplify(
            converted,
            _options.OutlineSimplificationToleranceUm,
            _options.MaxOutlinePointsPerCell,
            out int dropped);
        if (dropped > 0)
        {
            Warnings.Add(
                $"Cell '{cellName}': dropped {dropped} outline polygon(s) to stay within the " +
                $"{_options.MaxOutlinePointsPerCell} outline-point cap.");
        }
        return simplified;
    }

    /// <summary>
    /// Builds the raw-code snippet whose <c>component()</c> returns the loaded GDS
    /// cell RE-ANCHORED to the application's origin convention: <c>nd.load_gds</c>
    /// keeps the GDS cell's own origin, so the cell is wrapped and shifted by
    /// <c>-bbox.min</c> — afterwards its geometry bounding box starts at (0, 0),
    /// i.e. the origin sits at the bbox bottom-left (Nazca Y-up), which is the
    /// app-space bbox top-left the exporter's placement math anchors on
    /// (<c>NazcaCoordinateMapper</c>'s zero-offset fallback). The wrapper still
    /// exposes bbox/pins, so the raw-code preview contract
    /// (<c>render_component_preview.py</c>) is unaffected.
    /// <c>topcellsonly=False</c> is required: the imported cell is usually a
    /// SUBcell of the file's top cell, which the default top-cells-only lookup
    /// refuses to find.
    /// </summary>
    private static string BuildRawCode(string cellName)
    {
        // Escape for the double-quoted Python string literal: backslashes and
        // quotes are backslash-escaped; control characters (legal in a GDS
        // STRING) are replaced with '_' — a raw newline or NUL would break the
        // emitted Python source.
        string escaped = cellName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        escaped = new string(escaped.Select(c => char.IsControl(c) ? '_' : c).ToArray());
        return
            "import nazca as nd\n" +
            "\n" +
            $"# Loads GDS cell \"{escaped}\" and re-anchors it to the bbox bottom-left (Nazca Y-up), the\n" +
            "# app-space bbox top-left the exporter/preview placement math anchors on.\n" +
            $"# {GdsHierarchyImporter.GdsFileNameToken} is a placeholder: the service replaces it with the absolute\n" +
            "# path of the .gds file copied next to the user-PDK JSON. topcellsonly=False because the\n" +
            "# imported cell is usually a SUBcell of the file's top cell.\n" +
            "def component():\n" +
            $"    with nd.Cell(name=\"{escaped}_aligned\") as cell:\n" +
            $"        _loaded = nd.load_gds(filename=\"{GdsHierarchyImporter.GdsFileNameToken}\", cellname=\"{escaped}\", topcellsonly=False)\n" +
            "        _bb = _loaded.bbox\n" +
            "        _loaded.put(-_bb[0], -_bb[1])\n" +
            "    return cell\n";
    }
}
