using CAP_Core.Components.Core;

namespace CAP_Core.Components.Creation;

/// <summary>
/// Helpers for carrying per-instance layout overrides (e.g. Nazca raw-code overrides,
/// issue #720) along with group templates. Overrides live outside the Core layer, so
/// they are handled here as opaque JSON strings keyed by component identifier.
/// </summary>
public static class GroupTemplateOverrides
{
    /// <summary>
    /// Collects the override JSON for every non-group descendant of <paramref name="group"/>.
    /// </summary>
    /// <param name="group">The group whose children are queried.</param>
    /// <param name="overrideJsonProvider">
    /// Maps a component identifier to its serialized override, or null when the
    /// component has no override.
    /// </param>
    /// <returns>Component identifier → serialized override JSON, for overridden children only.</returns>
    public static Dictionary<string, string> Collect(
        ComponentGroup group,
        Func<string, string?> overrideJsonProvider)
    {
        var result = new Dictionary<string, string>();
        CollectRecursive(group, overrideJsonProvider, result);
        return result;
    }

    /// <summary>
    /// Builds the old→new identifier map between a template group and a deep copy of it.
    /// <see cref="ComponentGroup.DeepCopy"/> preserves child order (also in nested groups),
    /// so original and copy are correlated by index.
    /// </summary>
    /// <param name="original">The template group the copy was created from.</param>
    /// <param name="copy">The deep-copied instance.</param>
    /// <returns>Original child identifier → copied child identifier (non-group children only).</returns>
    public static Dictionary<string, string> BuildIdentifierMap(
        ComponentGroup original,
        ComponentGroup copy)
    {
        var map = new Dictionary<string, string>();
        MapRecursive(original, copy, map);
        return map;
    }

    private static void CollectRecursive(
        ComponentGroup group,
        Func<string, string?> provider,
        Dictionary<string, string> result)
    {
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nested)
            {
                CollectRecursive(nested, provider, result);
                continue;
            }

            var json = provider(child.Identifier);
            if (!string.IsNullOrWhiteSpace(json))
                result[child.Identifier] = json;
        }
    }

    private static void MapRecursive(
        ComponentGroup original,
        ComponentGroup copy,
        Dictionary<string, string> map)
    {
        var pairCount = Math.Min(original.ChildComponents.Count, copy.ChildComponents.Count);
        for (var i = 0; i < pairCount; i++)
        {
            var originalChild = original.ChildComponents[i];
            var copiedChild = copy.ChildComponents[i];

            if (originalChild is ComponentGroup originalNested
                && copiedChild is ComponentGroup copiedNested)
            {
                MapRecursive(originalNested, copiedNested, map);
                continue;
            }

            map[originalChild.Identifier] = copiedChild.Identifier;
        }
    }
}
