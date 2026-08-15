using CAP_Core.Components.Core;

namespace CAP.Avalonia.Services.GdsImport.LayerVisibility;

/// <summary>
/// One GDS (layer, datatype) pair in use on the canvas, with how many imported
/// shapes (outline polygons + tagged frozen paths) it carries. Feeds the
/// per-layer visibility list of the Imported Layers panel (issue #858).
/// </summary>
/// <param name="Layer">GDS layer number.</param>
/// <param name="DataType">GDS datatype.</param>
/// <param name="ShapeCount">Outline polygons plus tagged frozen paths on the pair.</param>
public sealed record DesignLayerUsage(int Layer, int DataType, int ShapeCount);

/// <summary>
/// Walks the placed components (recursing into groups) and collects every GDS
/// (layer, datatype) pair carried by imported outline polygons or by frozen
/// paths tagged with their import source layer — exactly the geometry the
/// per-layer view filter of <see cref="GdsLayerVisibilityState"/> applies to.
/// </summary>
public static class DesignLayerUsageCollector
{
    /// <summary>
    /// Collects the distinct (layer, datatype) pairs used by <paramref name="components"/>
    /// with their shape counts, ordered by layer then datatype.
    /// </summary>
    public static IReadOnlyList<DesignLayerUsage> Collect(IEnumerable<Component> components)
    {
        var counts = new Dictionary<(int Layer, int DataType), int>();
        foreach (var component in components)
            CollectFrom(component, counts);
        return counts
            .OrderBy(kv => kv.Key.Layer).ThenBy(kv => kv.Key.DataType)
            .Select(kv => new DesignLayerUsage(kv.Key.Layer, kv.Key.DataType, kv.Value))
            .ToList();
    }

    private static void CollectFrom(Component component, Dictionary<(int, int), int> counts)
    {
        if (component.OutlinePolygons is { } outlines)
        {
            foreach (var polygon in outlines)
                Increment(counts, polygon.Layer, polygon.DataType);
        }

        if (component is not ComponentGroup group)
            return;

        foreach (var frozenPath in group.InternalPaths)
        {
            if (frozenPath.Layer is int layer && frozenPath.DataType is int dataType)
                Increment(counts, layer, dataType);
        }

        foreach (var child in group.ChildComponents)
            CollectFrom(child, counts);
    }

    private static void Increment(Dictionary<(int, int), int> counts, int layer, int dataType)
    {
        counts.TryGetValue((layer, dataType), out int count);
        counts[(layer, dataType)] = count + 1;
    }
}
