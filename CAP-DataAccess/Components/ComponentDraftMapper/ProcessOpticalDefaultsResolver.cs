using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper
{
    /// <summary>
    /// Resolves the default optical waveguide width (µm) and GDS layer of a
    /// fabrication process, used to stamp <c>PhysicalPin.WaveguideWidthMicrometers</c>
    /// and <c>PhysicalPin.Layer</c> so the DRC-lite pin-mismatch rule fires on real
    /// designs. The default is the process' FIRST declared optical cross-section;
    /// its layer is the first cross-section layer name resolved to a GDS layer
    /// number via the process layer stack. Mirrors the
    /// <see cref="WaveguideBendRadiusResolver"/> pattern of turning an
    /// <see cref="ActiveProcessSelection"/> plus the loaded PDK drafts into a
    /// process parameter. Unresolvable data yields null — callers must keep the
    /// pin values null then, so legacy designs stay silent (no false positives).
    /// </summary>
    public static class ProcessOpticalDefaultsResolver
    {
        /// <summary>
        /// Resolves the default optical waveguide width and layer from a single
        /// process definition. Null process, no optical cross-section, or a
        /// cross-section layer name unknown to the layer stack yields null for
        /// the respective value.
        /// </summary>
        public static (double? WidthUm, int? Layer) Resolve(ProcessDefinition? process)
        {
            var xsection = process?.Xsections?.FirstOrDefault(x => x.Kind == XsectionKind.Optical);
            if (xsection == null)
                return (null, null);

            double? width = xsection.WidthUm > 0 ? xsection.WidthUm : null;
            int? layer = ResolveLayerNumber(process!, xsection);
            return (width, layer);
        }

        /// <summary>
        /// Resolves the defaults from the design's active process selection and the
        /// loaded PDK drafts: the first member PDK (in selection order) whose process
        /// declares an optical cross-section wins. Null selection/drafts or playground
        /// mode yields (null, null).
        /// </summary>
        /// <param name="active">The design's active process selection, or null when unset.</param>
        /// <param name="loadedPdks">All currently loaded PDK drafts.</param>
        public static (double? WidthUm, int? Layer) Resolve(
            ActiveProcessSelection? active, IReadOnlyList<PdkDraft>? loadedPdks)
        {
            if (active == null || active.IsPlayground || loadedPdks == null)
                return (null, null);

            foreach (var memberName in active.MemberPdkNames)
            {
                var process = loadedPdks.FirstOrDefault(
                    d => string.Equals(d.Name, memberName, StringComparison.OrdinalIgnoreCase))?.Process;
                var defaults = Resolve(process);
                if (defaults.WidthUm != null || defaults.Layer != null)
                    return defaults;
            }

            return (null, null);
        }

        /// <summary>The GDS layer number of the cross-section's first layer name, or null when unknown.</summary>
        private static int? ResolveLayerNumber(ProcessDefinition process, ProcessXsection xsection)
        {
            var layerName = xsection.Layers?.FirstOrDefault();
            if (layerName == null)
                return null;

            var layer = process.Layers?.FirstOrDefault(
                l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
            return layer?.Layer;
        }
    }
}
