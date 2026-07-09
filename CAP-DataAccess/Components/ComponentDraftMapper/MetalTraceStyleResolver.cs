using CAP_Core.Components.Process;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper
{
    /// <summary>
    /// Resolves the <see cref="MetalTraceStyle"/> for an electrical routing trace from a
    /// fabrication process: it picks the process's metal cross-section
    /// (<see cref="XsectionKind.Metal"/>) and maps its width and first referenced layer to a
    /// GDS layer/datatype. When no process declares a metal cross-section it returns
    /// <see cref="MetalTraceStyle.Default"/> so electrical connections are still drawn on a
    /// distinct metal layer instead of the optical waveguide layer (issue #682).
    /// </summary>
    public static class MetalTraceStyleResolver
    {
        /// <summary>
        /// Resolves the metal trace style from the member process definitions of the active
        /// process. The first metal cross-section found (in the given order) wins; its listed
        /// layer name is looked up in that same process's layer stack for the GDS layer/datatype.
        /// </summary>
        /// <param name="processes">Process definitions of the active process's member PDKs.</param>
        /// <returns>The resolved style, or <see cref="MetalTraceStyle.Default"/> when none defines metal.</returns>
        public static MetalTraceStyle Resolve(IEnumerable<ProcessDefinition?>? processes)
        {
            if (processes == null)
                return MetalTraceStyle.Default;

            foreach (var process in processes)
            {
                if (process == null)
                    continue;

                var metal = process.Xsections?.FirstOrDefault(x => x.Kind == XsectionKind.Metal);
                if (metal == null)
                    continue;

                return BuildStyle(process, metal);
            }

            return MetalTraceStyle.Default;
        }

        /// <summary>Convenience overload for a single process definition.</summary>
        /// <param name="process">The process definition, or null.</param>
        public static MetalTraceStyle Resolve(ProcessDefinition? process) =>
            Resolve(process == null ? null : new[] { process });

        /// <summary>
        /// Resolves the metal trace style for a design's active process by matching its member
        /// PDK names against the loaded PDK drafts and reading their process definitions — the
        /// same lookup the Fabrication Process dialog uses. Returns
        /// <see cref="MetalTraceStyle.Default"/> for Playground / no selection / no metal xsection.
        /// </summary>
        /// <param name="active">The design's active process selection.</param>
        /// <param name="loadedPdks">All currently loaded PDK drafts.</param>
        public static MetalTraceStyle Resolve(
            ActiveProcessSelection? active, IReadOnlyList<PdkDraft> loadedPdks)
        {
            if (active == null || loadedPdks == null)
                return MetalTraceStyle.Default;

            var definitions = active.MemberPdkNames
                .Select(name => loadedPdks.FirstOrDefault(
                    d => string.Equals(d.Name, name, System.StringComparison.OrdinalIgnoreCase))?.Process);

            return Resolve(definitions);
        }

        private static MetalTraceStyle BuildStyle(ProcessDefinition process, ProcessXsection metal)
        {
            var width = metal.WidthUm > 0 ? metal.WidthUm : MetalTraceStyle.DefaultWidthUm;
            var layer = ResolveLayer(process, metal);

            return new MetalTraceStyle
            {
                WidthUm = width,
                GdsLayer = layer?.Layer ?? MetalTraceStyle.DefaultGdsLayer,
                GdsDatatype = layer?.Datatype ?? 0,
            };
        }

        /// <summary>
        /// Finds the GDS layer for a metal cross-section: first the layer it explicitly lists,
        /// otherwise a metal-named layer in the stack (so a user can just add a "METAL" layer
        /// and a metal xsection without wiring them together). Null falls back to the default.
        /// </summary>
        private static ProcessLayer? ResolveLayer(ProcessDefinition process, ProcessXsection metal)
        {
            var layers = process.Layers;
            if (layers == null || layers.Count == 0)
                return null;

            var layerName = metal.Layers?.FirstOrDefault();
            if (layerName != null)
            {
                var named = layers.FirstOrDefault(l => l.Name == layerName);
                if (named != null)
                    return named;
            }

            return layers.FirstOrDefault(
                l => l.Name != null && l.Name.Contains("METAL", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
