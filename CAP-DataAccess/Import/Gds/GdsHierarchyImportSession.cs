using System.Globalization;

namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Per-import state for <see cref="GdsHierarchyImporter"/>: caches (cell
/// bounding boxes, flattened cells, pins, known-component resolutions) and the
/// warning sink, so each cell is flattened/resolved exactly once per import.
/// </summary>
internal sealed class GdsHierarchyImportSession
{
    /// <summary>Pin-frame size mismatch (µm) tolerated before warning about a known component's size.</summary>
    private const double SizeMismatchToleranceUm = 1.0;

    /// <summary>
    /// Per-pin deviation (µm) tolerated in a pin-anchored placement before the
    /// cell's pin labels are reported as not matching the template's pin layout.
    /// </summary>
    private const double PinMismatchToleranceUm = 1.0;

    private readonly GdsHierarchyImportOptions _options;
    private readonly string _topCellName;
    private readonly Dictionary<string, GdsBoundingBox> _bboxes = new();
    private readonly Dictionary<string, FlattenedGdsCell> _flattened = new();
    private readonly Dictionary<string, IReadOnlyList<DetectedPin>> _pins = new();
    private readonly Dictionary<string, KnownComponent?> _known = new();
    private readonly Dictionary<string, KnownCellPinAnchor?> _pinAnchors = new();
    private readonly HashSet<string> _sizeMismatchWarned = new();
    private readonly HashSet<string> _pinMismatchWarned = new();

    public GdsHierarchyImportSession(GdsLibrary library, string topCellName, GdsHierarchyImportOptions options)
    {
        Library = library;
        _topCellName = topCellName;
        _options = options;
        Flattener = new GdsCellFlattener(library);
    }

    public GdsLibrary Library { get; }

    public GdsCellFlattener Flattener { get; }

    public GdsHierarchyImportOptions Options => _options;

    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Informational notes (no action needed): known-component resolutions,
    /// skipped zero-geometry/export-artifact cells. Kept separate from
    /// <see cref="Warnings"/> so the UI can show them at info level instead of
    /// alarming the user about a normal import.
    /// </summary>
    public List<string> Infos { get; } = new();

    public GdsBoundingBox TopBBox => GetCellBBox(_topCellName);

    public GdsBoundingBox GetCellBBox(string cellName)
    {
        if (!_bboxes.TryGetValue(cellName, out var bbox))
            _bboxes[cellName] = bbox = Flattener.GetBoundingBox(cellName);
        return bbox;
    }

    public FlattenedGdsCell GetFlattened(string cellName)
    {
        if (!_flattened.TryGetValue(cellName, out var flat))
            _flattened[cellName] = flat = Flattener.Flatten(cellName);
        return flat;
    }

    /// <summary>
    /// The cell's pins in app-space of its own bbox: the cell's OWN port
    /// labels plus the edge heuristic over the fully flattened geometry. Names
    /// are normalized (<see cref="GdsPinNameNormalizer"/>) BEFORE caching, so
    /// the draft pins and the names used for connection reconstruction can
    /// never diverge (blank/duplicate names would otherwise mis-wire or poison
    /// the persisted PDK).
    /// </summary>
    public IReadOnlyList<DetectedPin> GetCellPins(string cellName, GdsBoundingBox bbox)
    {
        if (_pins.TryGetValue(cellName, out var cached))
            return cached;

        var detectionCell = new FlattenedGdsCell { CellName = cellName };
        detectionCell.Polygons.AddRange(GetFlattened(cellName).Polygons);
        detectionCell.Texts.AddRange(Library.Cells[cellName].Elements.OfType<GdsText>());
        var pins = GdsPinNameNormalizer.Normalize(
            GdsPinDetector.Detect(detectionCell, bbox, _options.PinDetection),
            $"Cell '{cellName}'",
            Warnings);
        _pins[cellName] = pins;
        return pins;
    }

    /// <summary>
    /// The circuit's external ports: the top cell's OWN port LABELS only, in
    /// app-space of the top bbox. Unlike drafts, no edge heuristic runs here
    /// — internal geometry ends at the layout boundary belong to instances,
    /// and treating them as ports would fabricate connections the designer
    /// never labeled (gdsfactory circuits expose ports via top-level labels).
    /// </summary>
    public IReadOnlyList<DetectedPin> GetTopLevelPorts()
    {
        var detectionCell = new FlattenedGdsCell { CellName = _topCellName };
        detectionCell.Texts.AddRange(Library.Cells[_topCellName].Elements.OfType<GdsText>());
        return GdsPinDetector.Detect(detectionCell, TopBBox, _options.PinDetection);
    }

    /// <summary>
    /// The top cell's OWN polygons on the configured route layers
    /// (<see cref="GdsHierarchyImportOptions.RouteLayers"/>) — the routing
    /// geometry our exporters flatten into the top cell — converted to app-space
    /// of the top bbox (Y-down, origin at the bbox top-left; the same frame
    /// <see cref="GdsInstancePinProjector.ProjectPlacedBoundsTopLeft"/> places
    /// instances in). Only the top cell's own elements qualify: geometry pulled
    /// in through references belongs to the placed instances, whose components
    /// already render their own outlines — importing it here too would
    /// double-draw every instance's waveguide. Polygons on any other layer
    /// (devrec, halos, pin markers) are not routing and stay out.
    /// </summary>
    public IReadOnlyList<GdsOutlinePolygon> GetTopCellWaveguidePolygons() =>
        GetTopCellPolygonsOnLayers(_options.RouteLayers);

    /// <summary>
    /// The top cell's OWN polygons on the configured METAL route layers
    /// (<see cref="GdsHierarchyImportOptions.MetalRouteLayers"/>) — the
    /// electrical routing our exporters flatten into the top cell — in the same
    /// app-space frame <see cref="GetTopCellWaveguidePolygons"/> uses.
    /// </summary>
    public IReadOnlyList<GdsOutlinePolygon> GetTopCellMetalPolygons() =>
        GetTopCellPolygonsOnLayers(_options.MetalRouteLayers);

    private IReadOnlyList<GdsOutlinePolygon> GetTopCellPolygonsOnLayers(
        IReadOnlyList<(int Layer, int Datatype)> layers)
    {
        var bbox = TopBBox;
        return Library.Cells[_topCellName].Elements
            .OfType<GdsPolygon>()
            .Where(p => layers.Contains((p.Layer, p.DataType)))
            .Select(p => new GdsOutlinePolygon
            {
                Layer = p.Layer,
                DataType = p.DataType,
                Points = p.Points
                    .Select(gp => new GdsOutlinePoint(gp.X - bbox.MinX, bbox.MaxY - gp.Y))
                    .ToList(),
            })
            .ToList();
    }

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
    /// exactly like for explode-mode drafts.
    /// Pin KINDS stay unknown (<see cref="DetectedPin.IsElectrical"/> null):
    /// geometry labels carry no signal-domain information (v1 limitation).
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
        for (int i = 0; i < flat.Texts.Count; i++)
        {
            var text = flat.Texts[i];
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

            detectionCell.Texts.Add(text with { Text = label });
        }

        return GdsPinNameNormalizer.Normalize(
            GdsPinDetector.Detect(detectionCell, bbox, _options.PinDetection),
            $"Cell '{cellName}'",
            Warnings);
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
    /// Resolves the cell to a known PDK component: exact name first, then
    /// gdsfactory hash-suffix-stripped candidates. Multiple distinct hits
    /// after stripping are ambiguous — never guessed, treated as unknown.
    /// </summary>
    public KnownComponent? ResolveKnown(string cellName)
    {
        if (_known.TryGetValue(cellName, out var cached))
            return cached;

        KnownComponent? result = null;
        var resolver = _options.ResolveKnownComponent;
        if (resolver is not null)
        {
            result = resolver(cellName);
            if (result is null)
            {
                var hits = HashStrippedCandidates(cellName)
                    .Select(candidate => resolver(candidate))
                    .Where(hit => hit is not null)
                    .DistinctBy(hit => (hit!.Identifier, hit.PdkSource))
                    .ToList();
                if (hits.Count == 1)
                {
                    result = hits[0];
                }
                else if (hits.Count > 1)
                {
                    Warnings.Add(
                        $"Cell name '{cellName}' matches {hits.Count} known components after " +
                        "stripping the gdsfactory hash suffix " +
                        $"({string.Join(", ", hits.Select(h => $"'{h!.Identifier}'"))}); " +
                        "ambiguous — treated as a new component draft.");
                }
            }
        }

        if (result is not null)
        {
            // Resolution visibility: the user must see which library component a
            // cell was bound to (especially when several PDKs provide the name).
            // Informational, not a warning — a successful binding is the norm.
            Infos.Add(
                $"Cell '{cellName}' resolved to existing component '{result.Identifier}' " +
                $"(PDK {result.PdkSource}).");
        }

        _known[cellName] = result;
        return result;
    }

    /// <summary>
    /// The pin-anchored placement frame for a cell resolved to a known component
    /// (<see cref="GdsInstancePinProjector.AnchorToTemplatePins"/>), or null when
    /// no template pin has a same-named label on the cell — the caller then keeps
    /// the bbox placement and the size-mismatch warning
    /// (<see cref="WarnOnSizeMismatchOnce"/>). Computed once per cell (every
    /// instance shares the cell-local delta). When the matched pins deviate past
    /// <see cref="PinMismatchToleranceUm"/> (a genuine pin-layout mismatch — the
    /// cell's pins are not a rigid translation of the template's), ONE warning
    /// per cell is emitted; the placement is still pin-anchored (best fit).
    /// </summary>
    public KnownCellPinAnchor? GetKnownCellPinAnchor(
        string cellName, KnownComponent known, GdsBoundingBox cellBBox)
    {
        if (_pinAnchors.TryGetValue(cellName, out var cached))
            return cached;

        var anchor = GdsInstancePinProjector.AnchorToTemplatePins(known, GetCellPins(cellName, cellBBox), cellBBox);
        if (anchor is not null
            && anchor.MaxDeviationUm > PinMismatchToleranceUm
            && _pinMismatchWarned.Add(cellName))
        {
            Warnings.Add(
                $"Known component '{known.Identifier}': the pin labels of GDS cell '{cellName}' do not match " +
                $"the template's pin layout (largest deviation {GdsHierarchyImporter.Fmt(Math.Round(anchor.MaxDeviationUm, 1))} µm " +
                $"at pin '{anchor.WorstPinName}') — placed pin-anchored (best fit); the reconstructed " +
                "connections may be geometrically incorrect.");
        }

        _pinAnchors[cellName] = anchor;
        return anchor;
    }

    /// <summary>
    /// The bbox-fallback size warning for a known-resolved cell, emitted once per
    /// cell. Only fires when NO template pin could be matched to a pin label on
    /// the cell — with matching labels the pins anchor the placement and a
    /// marker-inflated bbox (e.g. SiEPIC m_pin paths) is benign, so a pure size
    /// mismatch without pin evidence never reaches this method.
    /// </summary>
    public void WarnOnSizeMismatchOnce(string cellName, KnownComponent known, GdsBoundingBox cellBBox)
    {
        if (!_sizeMismatchWarned.Add(cellName))
            return;
        if (Math.Abs(known.WidthUm - cellBBox.Width) > SizeMismatchToleranceUm
            || Math.Abs(known.HeightUm - cellBBox.Height) > SizeMismatchToleranceUm)
        {
            Warnings.Add(
                $"Known component '{known.Identifier}' is {GdsHierarchyImporter.Fmt(known.WidthUm)}×{GdsHierarchyImporter.Fmt(known.HeightUm)} µm " +
                $"but GDS cell '{cellName}' measures {GdsHierarchyImporter.Fmt(cellBBox.Width)}×{GdsHierarchyImporter.Fmt(cellBBox.Height)} µm; " +
                "pin positions are mapped UNSCALED onto the GDS bounding box — the reconstructed " +
                "connections may be geometrically incorrect.");
        }
    }

    /// <summary>
    /// Yields the cell name with trailing gdsfactory hash suffixes removed,
    /// one strip at a time (e.g. "a_B1C2_D3E4" → "a_B1C2" → "a"). A suffix
    /// counts as a hash only when it is 4–16 pure hex characters, so names
    /// like "bend_euler" or "pad_20" are never stripped.
    /// </summary>
    private static IEnumerable<string> HashStrippedCandidates(string cellName)
    {
        var current = cellName;
        while (true)
        {
            int underscore = current.LastIndexOf('_');
            if (underscore <= 0)
                yield break;
            var suffix = current[(underscore + 1)..];
            if (suffix.Length is < 4 or > 16 || !suffix.All(IsHexDigit))
                yield break;
            current = current[..underscore];
            yield return current;
        }
    }

    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

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
