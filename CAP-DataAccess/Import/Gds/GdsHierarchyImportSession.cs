namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Per-import state for <see cref="GdsHierarchyImporter"/>: caches (cell
/// bounding boxes, flattened cells, pins, known-component resolutions) and the
/// warning sink, so each cell is flattened/resolved exactly once per import.
/// </summary>
internal sealed partial class GdsHierarchyImportSession
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
    /// the persisted PDK). Coincident label stacks collapse into one label
    /// first (<see cref="CollapseCoincidentLabels"/>). When no configured port
    /// layer yields any label pin, the any-layer fallback retries with every
    /// text label (<see cref="DetectWithAnyLayerFallback"/>).
    /// </summary>
    public IReadOnlyList<DetectedPin> GetCellPins(string cellName, GdsBoundingBox bbox)
    {
        if (_pins.TryGetValue(cellName, out var cached))
            return cached;

        var detectionCell = new FlattenedGdsCell { CellName = cellName };
        detectionCell.Polygons.AddRange(GetFlattened(cellName).Polygons);
        detectionCell.Texts.AddRange(CollapseCoincidentLabels(
            Library.Cells[cellName].Elements.OfType<GdsText>().Where(IsSingleLineLabel).ToList(),
            cellName));
        var pins = GdsPinNameNormalizer.Normalize(
            DetectWithAnyLayerFallback(detectionCell, bbox, cellName),
            $"Cell '{cellName}'",
            Warnings);
        pins = FilterExcludedGuessedPins(cellName, pins);
        _pins[cellName] = pins;
        return pins;
    }

    /// <summary>
    /// Removes heuristic pins the user deleted in the import dialog from the
    /// final pin list. Filtering happens after naming so the surviving pins
    /// keep the same names the dialog showed.
    /// </summary>
    private IReadOnlyList<DetectedPin> FilterExcludedGuessedPins(
        string cellName, IReadOnlyList<DetectedPin> pins)
    {
        if (_options.ExcludedGuessedPins.Count == 0)
            return pins;

        var excluded = new HashSet<(string Cell, string Pin)>(
            _options.ExcludedGuessedPins.Select(g => (g.CellName, g.PinName)));
        return pins.Where(p =>
            p.Source != DetectedPinSource.EdgeHeuristic
            || !excluded.Contains((cellName, p.Name))).ToList();
    }

    /// <summary>
    /// The circuit's external ports: the top cell's OWN port LABELS only, in
    /// app-space of the top bbox. Unlike drafts, no edge heuristic runs here
    /// — internal geometry ends at the layout boundary belong to instances,
    /// and treating them as ports would fabricate connections the designer
    /// never labeled (gdsfactory circuits expose ports via top-level labels).
    /// The any-layer label fallback (<see cref="DetectWithAnyLayerFallback"/>)
    /// deliberately does NOT apply here either: an unconfigured top-cell text
    /// is more likely a stray annotation than a circuit port, and a fabricated
    /// external port is worse than a missing one.
    /// </summary>
    public IReadOnlyList<DetectedPin> GetTopLevelPorts()
    {
        var detectionCell = new FlattenedGdsCell { CellName = _topCellName };
        detectionCell.Texts.AddRange(Library.Cells[_topCellName].Elements.OfType<GdsText>()
            .Where(IsSingleLineLabel));
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

    /// <summary>
    /// The top cell's OWN polygons on every layer that is NEITHER optical routing
    /// (<see cref="GdsHierarchyImportOptions.RouteLayers"/>) nor metal routing
    /// (<see cref="GdsHierarchyImportOptions.MetalRouteLayers"/>): substrate/base
    /// plates, exclusion zones, logos, markers. Real foundry designs carry such
    /// geometry directly in the top cell — dropping it made imports visibly
    /// incomplete. Simplified under the same outline-point cap as cell outlines
    /// (with a warning when polygons are dropped); same app-space frame as
    /// <see cref="GetTopCellWaveguidePolygons"/>.
    /// </summary>
    public IReadOnlyList<GdsOutlinePolygon> GetTopCellResidualPolygons()
    {
        var routingLayers = new HashSet<(int, int)>(
            _options.RouteLayers.Concat(_options.MetalRouteLayers));
        var converted = ConvertTopCellPolygons(p => !routingLayers.Contains((p.Layer, p.DataType)));
        if (converted.Count == 0)
            return converted;

        var simplified = GdsOutlineSimplifier.Simplify(
            converted,
            _options.OutlineSimplificationToleranceUm,
            _options.MaxOutlinePointsPerCell,
            out int dropped);
        if (dropped > 0)
        {
            Warnings.Add(
                $"Top cell '{_topCellName}': dropped {dropped} background polygon(s) to stay " +
                $"within the {_options.MaxOutlinePointsPerCell} outline-point cap.");
        }
        return simplified;
    }

    private IReadOnlyList<GdsOutlinePolygon> GetTopCellPolygonsOnLayers(
        IReadOnlyList<(int Layer, int Datatype)> layers) =>
        ConvertTopCellPolygons(p => layers.Contains((p.Layer, p.DataType)));

    private List<GdsOutlinePolygon> ConvertTopCellPolygons(Func<GdsPolygon, bool> keep)
    {
        var bbox = TopBBox;
        // PATH elements are expanded to outline quads: real PDK exports draw
        // most top-cell routing as PATHs, which the route matcher and the
        // frozen/residual collectors would otherwise never see.
        return GdsPathOutliner.ExpandDrawnGeometry(Library.Cells[_topCellName].Elements)
            .Where(keep)
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
        var candidatesResolver = _options.ResolveKnownComponentCandidates;
        if (candidatesResolver is not null)
        {
            result = ResolveViaCandidates(cellName, candidatesResolver);
        }
        else
        {
            var resolver = _options.ResolveKnownComponent;
            if (resolver is not null)
            {
                result = resolver(cellName);
                if (result is null)
                {
                    var hits = HashStrippedCandidates(cellName)
                        .Select(candidate => resolver(candidate))
                        .Where(hit => hit is not null)
                        // A hash-stripped name proves nothing about a parametric
                        // template's settings — only exact (bare-name) hits may bind
                        // those (bare name = default parameters in both nazca and
                        // gdsfactory naming).
                        .Where(hit => !hit!.IsParametric)
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
    /// Resolves via the candidate-list resolver: exact hits first, then
    /// hash-stripped candidates (parametric templates excluded there — a hash
    /// proves nothing about settings). Multiple distinct candidates are
    /// disambiguated by PIN-LAYOUT fit against the cell's detected pins; no
    /// fit within tolerance means unknown — never a guess.
    /// </summary>
    private KnownComponent? ResolveViaCandidates(
        string cellName, Func<string, IReadOnlyList<KnownComponent>> candidatesResolver)
    {
        var candidates = candidatesResolver(cellName);
        if (candidates.Count == 0)
        {
            candidates = HashStrippedCandidates(cellName)
                .SelectMany(candidate => candidatesResolver(candidate))
                .Where(hit => !hit.IsParametric)
                .DistinctBy(hit => (hit.Identifier, hit.PdkSource))
                .ToList();
        }
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        var bbox = GetCellBBox(cellName);
        var pins = GetCellPins(cellName, bbox);
        KnownComponent? best = null;
        double bestDeviation = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            double deviation = PositionalFitDeviation(candidate, pins);
            if (deviation < bestDeviation)
            {
                bestDeviation = deviation;
                best = candidate;
            }
        }
        if (best is not null && bestDeviation <= PinMismatchToleranceUm)
            return best;

        Warnings.Add(
            $"Cell name '{cellName}' matches {candidates.Count} known components " +
            $"({string.Join(", ", candidates.Select(c => $"'{c.Identifier}'"))}) and none fits the " +
            "cell's pin layout within tolerance — treated as a new component draft.");
        return null;
    }

    /// <summary>
    /// Name-free pin-layout fit: the smallest achievable worst-pin residual
    /// (µm) when the template's pin offsets are translated onto the cell's
    /// label-pin positions. Export paths rename pins (template "in/out1/out2"
    /// vs. cell labels "a0/b0/b1"), so names cannot disambiguate — positions
    /// can. Every template pin must land near a label; surplus labels are
    /// tolerated. O(pins²) — pin counts are tiny.
    /// </summary>
    private static double PositionalFitDeviation(KnownComponent candidate, IReadOnlyList<DetectedPin> cellPins)
    {
        var templatePoints = candidate.Pins.Select(p => (p.XUm, p.YUm)).ToList();
        var labelPoints = cellPins
            .Where(p => p.Source == DetectedPinSource.Label)
            .Select(p => (p.XUm, p.YUm))
            .ToList();
        if (templatePoints.Count == 0 || labelPoints.Count == 0)
            return double.PositiveInfinity;

        double best = double.PositiveInfinity;
        foreach (var t in templatePoints)
        {
            foreach (var c in labelPoints)
            {
                double dx = c.XUm - t.XUm, dy = c.YUm - t.YUm;
                double maxResidual = 0;
                foreach (var tp in templatePoints)
                {
                    double px = tp.XUm + dx, py = tp.YUm + dy;
                    double nearest = labelPoints.Min(cp =>
                    {
                        double ddx = cp.XUm - px, ddy = cp.YUm - py;
                        return Math.Sqrt(ddx * ddx + ddy * ddy);
                    });
                    maxResidual = Math.Max(maxResidual, nearest);
                }
                best = Math.Min(best, maxResidual);
            }
        }
        return best;
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
}
