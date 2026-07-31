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

    private readonly GdsHierarchyImportOptions _options;
    private readonly string _topCellName;
    private readonly Dictionary<string, GdsBoundingBox> _bboxes = new();
    private readonly Dictionary<string, FlattenedGdsCell> _flattened = new();
    private readonly Dictionary<string, IReadOnlyList<DetectedPin>> _pins = new();
    private readonly Dictionary<string, KnownComponent?> _known = new();
    private readonly HashSet<string> _sizeMismatchWarned = new();

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
    /// labels plus the edge heuristic over the fully flattened geometry.
    /// </summary>
    public IReadOnlyList<DetectedPin> GetCellPins(string cellName, GdsBoundingBox bbox)
    {
        if (_pins.TryGetValue(cellName, out var cached))
            return cached;

        var detectionCell = new FlattenedGdsCell { CellName = cellName };
        detectionCell.Polygons.AddRange(GetFlattened(cellName).Polygons);
        detectionCell.Texts.AddRange(Library.Cells[cellName].Elements.OfType<GdsText>());
        var pins = GdsPinDetector.Detect(detectionCell, bbox, _options.PinDetection);
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

        _known[cellName] = result;
        return result;
    }

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
                "pin positions are mapped onto the GDS bounding box.");
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

    private static string BuildRawCode(string cellName)
    {
        string escaped = cellName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return
            "import nazca as nd\n" +
            "\n" +
            $"# Loads GDS cell \"{escaped}\". {GdsHierarchyImporter.GdsFileNameToken} is a placeholder: the UI replaces it\n" +
            "# with the .gds file name copied next to the user-PDK JSON.\n" +
            "def component():\n" +
            $"    return nd.load_gds(filename=\"{GdsHierarchyImporter.GdsFileNameToken}\", cellname=\"{escaped}\")\n";
    }
}
