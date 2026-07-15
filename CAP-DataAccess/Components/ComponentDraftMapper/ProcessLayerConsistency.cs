using System.Collections.Generic;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper;

public static class ProcessLayerConsistency
{
    public static bool LayersConsistent(ProcessDefinition? a, ProcessDefinition? b)
    {
        if (a is null || b is null || a.Layers.Count == 0 || b.Layers.Count == 0)
            return true;

        var aByName = FirstOccurrenceByName(a.Layers);
        var bByName = FirstOccurrenceByName(b.Layers);

        foreach (var (name, aLayer) in aByName)
        {
            if (bByName.TryGetValue(name, out var bLayer) &&
                (aLayer.Layer != bLayer.Layer || aLayer.Datatype != bLayer.Datatype))
                return false;
        }

        return true;
    }

    private static Dictionary<string, ProcessLayer> FirstOccurrenceByName(List<ProcessLayer> layers)
    {
        var result = new Dictionary<string, ProcessLayer>();
        foreach (var layer in layers)
        {
            var name = NormalizedName(layer.Name);
            if (name.Length == 0)
                continue;
            if (!result.ContainsKey(name))
                result[name] = layer;
        }
        return result;
    }

    private static string NormalizedName(string? name) => (name ?? string.Empty).Trim().ToUpperInvariant();
}
