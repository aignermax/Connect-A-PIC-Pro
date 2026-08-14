namespace CAP_DataAccess.Import.Gds.LayerCensus;

/// <summary>
/// Derives visible, user-confirmable layer-assignment suggestions from a layer
/// census, strongest evidence first: port-attachment proof (port labels
/// resting on a layer's shapes make it optical — high, and vetoes any
/// metal convention claim on the same pair), text-backed port-label layers
/// (high), metal/waveguide claims from the union of known tool/foundry tables
/// (medium — layer numbers collide across foundries: one foundry's M1 is
/// another's core etch, so optical vs electrical from a bare number stays a
/// human-confirmed guess), port-layer polygon shapes that touch a cell boundary
/// (low — geometry-only hint for marker layers that carry no text), and
/// top-cell layers with route-like strokes as "routing, kind unknown" (low).
/// Facts stay in the census; only high-confidence suggestions are auto-applied
/// by the dialog — everything else waits for a click.
/// </summary>
public static class GdsLayerSuggestionEngine
{
    /// <summary>
    /// A top-cell polygon whose bounding box is at least this many times longer
    /// than it is wide counts as a route-like stroke.
    /// </summary>
    private const double RouteStrokeAspectRatio = 8.0;

    /// <summary>Cap on the cell names listed in a text-evidence reason.</summary>
    private const int MaxCellNamesInReason = 4;

    /// <summary>Cell bounding-box edges used to reject full-frame outlines.</summary>
    private enum BoundaryEdge { Left, Top, Right, Bottom }

    /// <summary>
    /// Builds the suggestions for one top-cell choice. Deterministic: at most
    /// one suggestion per (pair, role), known conventions first, then text
    /// evidence, then route heuristics; each group sorted by layer/datatype.
    /// </summary>
    /// <param name="library">The parsed library the census came from.</param>
    /// <param name="topCellName">The top cell whose drawn routes are inspected.</param>
    /// <param name="census">The library's layer census (<see cref="GdsLayerCensus.Build"/>).</param>
    public static IReadOnlyList<GdsLayerSuggestion> Build(
        GdsLibrary library, string topCellName, IReadOnlyList<GdsLayerCensusEntry> census)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(census);

        var suggestions = new List<GdsLayerSuggestion>();
        var covered = new HashSet<(int Layer, int Datatype, GdsLayerRole Role)>();

        var opticalProven = AddPortAttachmentSuggestions(library, suggestions, covered);
        AddKnownConventionMatches(census, suggestions, covered, opticalProven);
        AddTextBearingLayers(census, suggestions, covered);
        AddPortShapeLayers(library, suggestions, covered);
        AddTopCellRouteCandidates(library, topCellName, suggestions, covered);
        return suggestions;
    }

    /// <summary>
    /// The strongest evidence available: port labels resting on a layer's
    /// shapes prove it optical (a label sits at the end of the shape it names)
    /// — configuration-independent, so it stays right when the convention
    /// tables and defaults are wrong for the file. Returns the proven-optical
    /// pairs so the convention matcher can veto its metal claims on them.
    /// </summary>
    private static HashSet<(int, int)> AddPortAttachmentSuggestions(
        GdsLibrary library,
        List<GdsLayerSuggestion> suggestions,
        HashSet<(int, int, GdsLayerRole)> covered)
    {
        var optical = new HashSet<(int, int)>();
        foreach (var (pair, evidence) in GdsPortAttachmentProbe.Scan(library))
        {
            if (!covered.Add((pair.Layer, pair.Datatype, GdsLayerRole.Waveguide)))
                continue;
            // A single resting label is a hint; a quorum is proof (and only
            // proof vetoes a metal convention claim on the same pair).
            var confidence = evidence.Count >= GdsPortAttachmentProbe.MinVotesForProof
                ? GdsSuggestionConfidence.High
                : GdsSuggestionConfidence.Medium;
            if (confidence == GdsSuggestionConfidence.High)
                optical.Add(pair);
            suggestions.Add(new GdsLayerSuggestion(
                pair.Layer, pair.Datatype, GdsLayerRole.Waveguide,
                confidence,
                $"{evidence.Count} port label(s) rest on this layer ({FormatLabelSample(evidence.SampleLabels)})"));
        }
        return optical;
    }

    private static string FormatLabelSample(IReadOnlyList<string> labels) =>
        string.Join(", ", labels.Select(l => $"'{l}'"));

    private static void AddKnownConventionMatches(
        IReadOnlyList<GdsLayerCensusEntry> census,
        List<GdsLayerSuggestion> suggestions,
        HashSet<(int, int, GdsLayerRole)> covered,
        HashSet<(int, int)> opticalProven)
    {
        foreach (var entry in census)
        {
            foreach (var known in GdsKnownLayerTables.Match(entry.Layer, entry.Datatype))
            {
                // Port attachment beats convention: a layer the ports rest on is
                // optical, whatever number another foundry uses for its metal.
                if (known.Role == GdsLayerRole.Metal
                    && opticalProven.Contains((entry.Layer, entry.Datatype)))
                    continue;
                if (!RoleFitsContent(known.Role, entry))
                    continue;
                if (covered.Add((entry.Layer, entry.Datatype, known.Role)))
                {
                    suggestions.Add(new GdsLayerSuggestion(
                        entry.Layer, entry.Datatype, known.Role,
                        ConfidenceFor(known.Role), known.Source));
                }
            }
        }
    }

    /// <summary>
    /// Port-label conventions carry double evidence (the number matches a known
    /// table AND the layer actually bears texts) — high. Metal/waveguide claims
    /// from the union table are only medium: layer numbers collide across
    /// foundries, so a bare-number optical/electrical claim is a convention
    /// guess the user confirms — it must never silently misroute waveguides
    /// onto the metal field.
    /// </summary>
    private static GdsSuggestionConfidence ConfidenceFor(GdsLayerRole role) =>
        role == GdsLayerRole.PortLabels
            ? GdsSuggestionConfidence.High
            : GdsSuggestionConfidence.Medium;

    /// <summary>A convention only applies when the pair actually carries matching content.</summary>
    private static bool RoleFitsContent(GdsLayerRole role, GdsLayerCensusEntry entry) => role switch
    {
        GdsLayerRole.PortLabels => entry.PortLikeTextCount > 0,
        _ => entry.PolygonCount + entry.PathCount > 0,
    };

    private static void AddTextBearingLayers(
        IReadOnlyList<GdsLayerCensusEntry> census,
        List<GdsLayerSuggestion> suggestions,
        HashSet<(int, int, GdsLayerRole)> covered)
    {
        foreach (var entry in census.Where(e => e.PortLikeTextCount > 0))
        {
            if (!covered.Add((entry.Layer, entry.Datatype, GdsLayerRole.PortLabels)))
                continue;
            // Single-line, non-ghost text labels on a layer ARE the port-label
            // evidence — strong enough to auto-apply (helper/anchor labels are
            // filtered here already and again downstream at pin detection).
            suggestions.Add(new GdsLayerSuggestion(
                entry.Layer, entry.Datatype, GdsLayerRole.PortLabels,
                GdsSuggestionConfidence.High,
                $"{entry.PortLikeTextCount} text label(s) in {FormatCellList(entry.TextCellNames)}"));
        }
    }

    /// <summary>
    /// Layers that carry polygons touching a cell boundary are plausible port-
    /// marker layers even when they have no text (e.g. gdsfactory-style pin
    /// markers drawn as small polygons). The suggestion is deliberately low
    /// confidence: a boundary-touching rectangle can also be an outline or a
    /// pad, so it is offered but never auto-applied.
    /// </summary>
    private static void AddPortShapeLayers(
        GdsLibrary library,
        List<GdsLayerSuggestion> suggestions,
        HashSet<(int Layer, int Datatype, GdsLayerRole Role)> covered)
    {
        const double toleranceUm = 0.01;
        var counts = new Dictionary<(int Layer, int Datatype), int>();
        var cellNames = new Dictionary<(int Layer, int Datatype), HashSet<string>>();

        foreach (var cell in library.Cells.Values)
        {
            var bbox = ComputeCellBoundingBox(cell);
            if (bbox.Width <= 0 || bbox.Height <= 0)
                continue;

            foreach (var element in cell.Elements)
            {
                if (element is not GdsPolygon polygon)
                    continue;

                // Full-cell outlines (DevRec/bbox frames) touch three or four
                // edges and are not port markers — skip them before counting.
                var touchedEdges = new HashSet<BoundaryEdge>();
                for (int i = 0; i < polygon.Points.Count - 1; i++)
                {
                    var p1 = polygon.Points[i];
                    var p2 = polygon.Points[i + 1];
                    if (BoundaryEdgeTouched(p1, p2, bbox, toleranceUm) is { } edge)
                        touchedEdges.Add(edge);
                }
                if (touchedEdges.Count >= 3)
                    continue;

                for (int i = 0; i < polygon.Points.Count - 1; i++)
                {
                    var p1 = polygon.Points[i];
                    var p2 = polygon.Points[i + 1];
                    if (BoundaryEdgeTouched(p1, p2, bbox, toleranceUm) is null)
                        continue;

                    var key = (polygon.Layer, polygon.DataType);
                    counts[key] = counts.TryGetValue(key, out var value) ? value + 1 : 1;
                    if (!cellNames.TryGetValue(key, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        cellNames[key] = set;
                    }
                    set.Add(cell.Name);
                }
            }
        }

        foreach (var (pair, count) in counts
            .OrderBy(kv => kv.Key.Layer)
            .ThenBy(kv => kv.Key.Datatype))
        {
            // A pair already claimed as waveguide/metal is route geometry —
            // a through-going core strip touches the bbox left+right and must
            // not additionally get a "may mark ports" chip.
            if (covered.Contains((pair.Layer, pair.Datatype, GdsLayerRole.Waveguide))
                || covered.Contains((pair.Layer, pair.Datatype, GdsLayerRole.Metal))
                || !covered.Add((pair.Layer, pair.Datatype, GdsLayerRole.PortLabels)))
            {
                continue;
            }

            var reason = $"{count} boundary-touching polygon(s) in {FormatCellList(cellNames[pair].ToList())} may mark ports";
            suggestions.Add(new GdsLayerSuggestion(
                pair.Layer, pair.Datatype, GdsLayerRole.PortLabels,
                GdsSuggestionConfidence.Low, reason));
        }
    }

    /// <summary>
    /// The cell's own bounding box, built from its direct polygon/path/text
    /// geometry. References are ignored: port markers are part of the cell's
    /// own geometry, and resolving transformed references would require a full
    /// flatten pass the census deliberately avoids.
    /// </summary>
    private static GdsBoundingBox ComputeCellBoundingBox(GdsCell cell)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool hasPoint = false;

        void Consider(GdsPoint point)
        {
            hasPoint = true;
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        foreach (var element in cell.Elements)
        {
            switch (element)
            {
                case GdsPolygon polygon:
                    foreach (var point in polygon.Points)
                        Consider(point);
                    break;
                case GdsPath path:
                    foreach (var point in path.Points)
                        Consider(point);
                    break;
                case GdsText text:
                    Consider(text.Position);
                    break;
                case GdsReference reference:
                    Consider(reference.Offset);
                    break;
            }
        }

        return hasPoint
            ? new GdsBoundingBox(minX, minY, maxX, maxY)
            : GdsBoundingBox.Empty;
    }

    /// <summary>
    /// The bounding-box edge a segment lies on (both endpoints within
    /// <paramref name="toleranceUm"/> of the edge line), or null. Edges are
    /// checked in a fixed order so a corner segment matches at most one edge.
    /// </summary>
    private static BoundaryEdge? BoundaryEdgeTouched(
        GdsPoint p1, GdsPoint p2, GdsBoundingBox bbox, double toleranceUm)
    {
        if (Math.Abs(p1.X - bbox.MinX) <= toleranceUm && Math.Abs(p2.X - bbox.MinX) <= toleranceUm)
            return BoundaryEdge.Left;
        if (Math.Abs(p1.Y - bbox.MaxY) <= toleranceUm && Math.Abs(p2.Y - bbox.MaxY) <= toleranceUm)
            return BoundaryEdge.Top;
        if (Math.Abs(p1.X - bbox.MaxX) <= toleranceUm && Math.Abs(p2.X - bbox.MaxX) <= toleranceUm)
            return BoundaryEdge.Right;
        if (Math.Abs(p1.Y - bbox.MinY) <= toleranceUm && Math.Abs(p2.Y - bbox.MinY) <= toleranceUm)
            return BoundaryEdge.Bottom;
        return null;
    }

    /// <summary>
    /// Pairs whose top-cell elements look like drawn routes: PATH elements, or
    /// long thin polygons (aspect ratio ≥ <see cref="RouteStrokeAspectRatio"/>).
    /// Pairs already suggested as waveguide or metal by a known convention are
    /// skipped — the convention is the stronger, role-resolved claim.
    /// </summary>
    private static void AddTopCellRouteCandidates(
        GdsLibrary library, string topCellName,
        List<GdsLayerSuggestion> suggestions,
        HashSet<(int, int, GdsLayerRole)> covered)
    {
        if (!library.Cells.TryGetValue(topCellName, out var topCell))
            return;

        var strokes = new Dictionary<(int Layer, int Datatype), int>();
        foreach (var element in topCell.Elements)
        {
            switch (element)
            {
                case GdsPath path:
                    Increment(strokes, (path.Layer, path.DataType));
                    break;
                case GdsPolygon polygon when IsStrokeLike(polygon):
                    Increment(strokes, (polygon.Layer, polygon.DataType));
                    break;
            }
        }

        foreach (var (pair, count) in strokes.OrderBy(kv => kv.Key.Layer).ThenBy(kv => kv.Key.Datatype))
        {
            if (covered.Contains((pair.Layer, pair.Datatype, GdsLayerRole.Waveguide))
                || covered.Contains((pair.Layer, pair.Datatype, GdsLayerRole.Metal))
                || !covered.Add((pair.Layer, pair.Datatype, GdsLayerRole.RoutingUnknown)))
            {
                continue;
            }
            suggestions.Add(new GdsLayerSuggestion(
                pair.Layer, pair.Datatype, GdsLayerRole.RoutingUnknown,
                GdsSuggestionConfidence.Low,
                $"{count} route-like stroke(s) in top cell '{topCellName}'"));
        }
    }

    /// <summary>Long-thin bounding box ⇒ the polygon looks like a drawn route stroke.</summary>
    private static bool IsStrokeLike(GdsPolygon polygon)
    {
        if (polygon.Points.Count < 3)
            return false;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in polygon.Points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        double longSide = Math.Max(maxX - minX, maxY - minY);
        double shortSide = Math.Min(maxX - minX, maxY - minY);
        return shortSide > 0 && longSide / shortSide >= RouteStrokeAspectRatio;
    }

    private static void Increment(Dictionary<(int, int), int> counts, (int, int) key) =>
        counts[key] = counts.TryGetValue(key, out var value) ? value + 1 : 1;

    private static string FormatCellList(IReadOnlyList<string> cellNames)
    {
        if (cellNames.Count == 0)
            return "the file";
        var shown = string.Join(", ", cellNames.Take(MaxCellNamesInReason).Select(n => $"'{n}'"));
        var suffix = cellNames.Count > MaxCellNamesInReason
            ? $" (+{cellNames.Count - MaxCellNamesInReason} more)"
            : string.Empty;
        return $"cell(s) {shown}{suffix}";
    }
}
