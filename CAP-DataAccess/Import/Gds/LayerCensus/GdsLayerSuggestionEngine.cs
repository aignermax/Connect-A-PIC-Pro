namespace CAP_DataAccess.Import.Gds.LayerCensus;

/// <summary>
/// Derives visible, user-confirmable layer-assignment suggestions from a layer
/// census: known tool/foundry conventions (high confidence), text-bearing
/// layers as port-label candidates (medium), and top-cell layers with
/// route-like strokes as "routing, kind unknown" (low — optical vs metal is
/// undecidable from geometry alone). Facts stay in the census; everything
/// produced here is a labeled guess the user must accept explicitly.
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

        AddKnownConventionMatches(census, suggestions, covered);
        AddTextBearingLayers(census, suggestions, covered);
        AddTopCellRouteCandidates(library, topCellName, suggestions, covered);
        return suggestions;
    }

    private static void AddKnownConventionMatches(
        IReadOnlyList<GdsLayerCensusEntry> census,
        List<GdsLayerSuggestion> suggestions,
        HashSet<(int, int, GdsLayerRole)> covered)
    {
        foreach (var entry in census)
        {
            foreach (var known in GdsKnownLayerTables.Match(entry.Layer, entry.Datatype))
            {
                if (!RoleFitsContent(known.Role, entry))
                    continue;
                if (covered.Add((entry.Layer, entry.Datatype, known.Role)))
                {
                    suggestions.Add(new GdsLayerSuggestion(
                        entry.Layer, entry.Datatype, known.Role,
                        GdsSuggestionConfidence.High, known.Source));
                }
            }
        }
    }

    /// <summary>A convention only applies when the pair actually carries matching content.</summary>
    private static bool RoleFitsContent(GdsLayerRole role, GdsLayerCensusEntry entry) => role switch
    {
        GdsLayerRole.PortLabels => entry.SingleLineTextCount > 0,
        _ => entry.PolygonCount + entry.PathCount > 0,
    };

    private static void AddTextBearingLayers(
        IReadOnlyList<GdsLayerCensusEntry> census,
        List<GdsLayerSuggestion> suggestions,
        HashSet<(int, int, GdsLayerRole)> covered)
    {
        foreach (var entry in census.Where(e => e.SingleLineTextCount > 0))
        {
            if (!covered.Add((entry.Layer, entry.Datatype, GdsLayerRole.PortLabels)))
                continue;
            suggestions.Add(new GdsLayerSuggestion(
                entry.Layer, entry.Datatype, GdsLayerRole.PortLabels,
                GdsSuggestionConfidence.Medium,
                $"{entry.SingleLineTextCount} text label(s) in {FormatCellList(entry.TextCellNames)}"));
        }
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
