using CAP_Core.Components.Process;
using CAP_Core.Routing.MetalRouting;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper
{
    /// <summary>
    /// Derives the <see cref="MetalRoutingSpec"/> for electrical metal routing (issue #682)
    /// from the active process' PDK definitions: trace width and GDS layer come from the
    /// first metal cross-section, the waveguide-crossing policy from the process'
    /// <see cref="ProcessDefinition.ElectricalBridgeRequired"/> flag. Falls back to
    /// <see cref="MetalRoutingSpec.Default"/> values when the process declares no metal data.
    /// </summary>
    public static class MetalRoutingSpecFactory
    {
        /// <summary>
        /// Builds the spec for the design's active process from the loaded PDK drafts.
        /// Null selection (no design yet) or playground mode yields the default spec.
        /// </summary>
        public static MetalRoutingSpec FromActiveProcess(
            ActiveProcessSelection? active, IReadOnlyList<PdkDraft>? loadedPdks)
        {
            if (active == null || loadedPdks == null)
                return MetalRoutingSpec.Default;

            var definitions = active.MemberPdkNames
                .Select(name => loadedPdks.FirstOrDefault(
                    d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.Process)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();
            return FromDefinitions(definitions);
        }

        /// <summary>
        /// Builds the spec from process definitions directly. The first definition that
        /// declares a metal cross-section supplies width and layer; the bridge policy is
        /// required as soon as ANY member definition demands it (conservative union).
        /// </summary>
        public static MetalRoutingSpec FromDefinitions(IReadOnlyList<ProcessDefinition> definitions)
        {
            var policy = definitions.Any(d => d.ElectricalBridgeRequired == true)
                ? ElectricalCrossingPolicy.BridgeRequired
                : ElectricalCrossingPolicy.DirectCrossingAllowed;

            foreach (var definition in definitions)
            {
                var metal = definition.Xsections.FirstOrDefault(x => x.Kind == XsectionKind.Metal);
                if (metal == null)
                    continue;

                var width = metal.WidthUm > 0
                    ? metal.WidthUm
                    : MetalRoutingSpec.DefaultTraceWidthMicrometers;
                var (layer, datatype) = ResolveMetalLayer(definition, metal);
                return new MetalRoutingSpec(
                    width, layer, datatype, policy, MetalRoutingSpec.DefaultBridgeGdsLayer);
            }

            return MetalRoutingSpec.Default with { CrossingPolicy = policy };
        }

        /// <summary>
        /// Resolves the GDS layer/datatype of a metal cross-section by looking its first
        /// layer name up in the process layer stack; falls back to the default metal layer
        /// when the cross-section names no known layer.
        /// </summary>
        private static (int Layer, int Datatype) ResolveMetalLayer(
            ProcessDefinition definition, ProcessXsection metal)
        {
            var layer = metal.Layers
                .Select(name => definition.Layers.FirstOrDefault(
                    l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(l => l != null);
            return layer != null
                ? (layer.Layer, layer.Datatype)
                : (MetalRoutingSpec.DefaultMetalGdsLayer, MetalRoutingSpec.DefaultMetalGdsDatatype);
        }
    }
}
