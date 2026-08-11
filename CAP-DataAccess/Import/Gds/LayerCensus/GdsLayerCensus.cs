namespace CAP_DataAccess.Import.Gds.LayerCensus;

/// <summary>
/// Facts about one (layer, datatype) pair present in a GDS library: how many
/// polygons, paths and texts live on it, and which cells carry the texts.
/// Pure census data — no interpretation (that is
/// <see cref="GdsLayerSuggestionEngine"/>'s job).
/// </summary>
public sealed record GdsLayerCensusEntry
{
    /// <summary>GDS layer number.</summary>
    public int Layer { get; init; }

    /// <summary>GDS datatype (texttype for TEXT elements).</summary>
    public int Datatype { get; init; }

    /// <summary>BOUNDARY/BOX polygons on this pair, across all cells.</summary>
    public int PolygonCount { get; init; }

    /// <summary>PATH elements on this pair, across all cells.</summary>
    public int PathCount { get; init; }

    /// <summary>TEXT elements on this pair, across all cells.</summary>
    public int TextCount { get; init; }

    /// <summary>
    /// TEXT elements whose string has no line break. Multi-line texts are
    /// metadata blobs (e.g. nazca's cell-info block), never port labels — the
    /// suggestion engine only treats a pair as a port-label candidate when this
    /// count is positive.
    /// </summary>
    public int SingleLineTextCount { get; init; }

    /// <summary>
    /// Single-line TEXT elements that additionally survive the ghost filter
    /// (<c>GdsGhostLabelFilter</c>): no bbox anchors (tl/tr/…), no parameter
    /// annotations (R:0.0001), no empty strings. This is the count that backs
    /// port-label suggestions — a layer carrying ONLY helper labels must not
    /// become a port-layer suggestion.
    /// </summary>
    public int PortLikeTextCount { get; init; }

    /// <summary>Names of the cells that carry TEXT elements on this pair, sorted.</summary>
    public IReadOnlyList<string> TextCellNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Builds the layer census of a parsed <see cref="GdsLibrary"/>: one entry per
/// (layer, datatype) pair that appears anywhere in the file, with element
/// counts. A single pass over all cells — cheap enough to run as part of the
/// import dialog's analysis stage.
/// </summary>
public static class GdsLayerCensus
{
    /// <summary>Counts all elements per (layer, datatype) pair; entries sorted by layer, then datatype.</summary>
    public static IReadOnlyList<GdsLayerCensusEntry> Build(GdsLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var stats = new Dictionary<(int Layer, int Datatype), MutableStats>();

        foreach (var cell in library.Cells.Values)
        {
            foreach (var element in cell.Elements)
                CountElement(stats, cell.Name, element);
        }

        return stats
            .OrderBy(kv => kv.Key.Layer)
            .ThenBy(kv => kv.Key.Datatype)
            .Select(kv => new GdsLayerCensusEntry
            {
                Layer = kv.Key.Layer,
                Datatype = kv.Key.Datatype,
                PolygonCount = kv.Value.Polygons,
                PathCount = kv.Value.Paths,
                TextCount = kv.Value.Texts,
                SingleLineTextCount = kv.Value.SingleLineTexts,
                PortLikeTextCount = kv.Value.PortLikeTexts,
                TextCellNames = kv.Value.TextCells.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            })
            .ToList();
    }

    private static void CountElement(
        Dictionary<(int, int), MutableStats> stats, string cellName, GdsElement element)
    {
        switch (element)
        {
            case GdsPolygon polygon:
                Get(stats, (polygon.Layer, polygon.DataType)).Polygons++;
                break;
            case GdsPath path:
                Get(stats, (path.Layer, path.DataType)).Paths++;
                break;
            case GdsText text:
                var entry = Get(stats, (text.Layer, text.TextType));
                entry.Texts++;
                if (!text.Text.Contains('\n'))
                {
                    entry.SingleLineTexts++;
                    if (text.Text.Trim().Length > 0 && !GdsGhostLabelFilter.IsGhost(text))
                        entry.PortLikeTexts++;
                }
                entry.TextCells.Add(cellName);
                break;
        }
    }

    private static MutableStats Get(Dictionary<(int, int), MutableStats> stats, (int, int) key)
    {
        if (!stats.TryGetValue(key, out var value))
        {
            value = new MutableStats();
            stats[key] = value;
        }
        return value;
    }

    private sealed class MutableStats
    {
        public int Polygons;
        public int Paths;
        public int Texts;
        public int SingleLineTexts;
        public int PortLikeTexts;
        public readonly HashSet<string> TextCells = new(StringComparer.Ordinal);
    }
}
