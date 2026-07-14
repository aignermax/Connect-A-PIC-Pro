using System.Collections.Generic;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper;

/// <summary>
/// Decides whether two <see cref="ProcessDefinition"/>s' GDS layer stacks physically conflict
/// (issue #570). <see cref="CAP_Core.Components.Process.ProcessCompatibility"/> compares only
/// materials/thickness/wavelength; it cannot see layer numbers because <c>ProcessDefinition</c>
/// lives in <c>CAP-DataAccess</c>, which references <c>CAP_Core</c> (not the other way around).
/// This type lives here — next to <see cref="ProcessFingerprintFactory"/> — so both the
/// fingerprint check and this layer check are reachable from a caller that can see both projects
/// (e.g. <c>LeftPanelViewModel</c>), without Core taking a dependency on DataAccess DTOs.
/// </summary>
public static class ProcessLayerConsistency
{
    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> do not disagree on any shared GDS
    /// layer. Null, or a definition with no layers, is treated as compatible — there is nothing
    /// to conflict with (a custom PDK author may not have filled in a layer stack yet). For every
    /// layer NAME (case-insensitive, trimmed) present in BOTH definitions, its (Layer, Datatype)
    /// pair must be identical; a layer present in only one definition is always allowed (e.g. a
    /// metal cross-section layer added on top of an otherwise-identical process, issue #734 —
    /// additions must stay compatible, only renumbered/repurposed shared layers diverge).
    /// <para>
    /// Duplicate layer names within one definition are resolved first-occurrence-wins rather than
    /// requiring every duplicate pair to agree with each other: an internal duplicate is a
    /// separate (PDK-authoring) concern from cross-process compatibility, which is the only thing
    /// this method decides.
    /// </para>
    /// </summary>
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
