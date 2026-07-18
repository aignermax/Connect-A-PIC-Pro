using CAP_Core.Components.Process;
using CAP_Core.Routing.InterconnectRouting;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper
{
    /// <summary>
    /// Resolves the minimum allowed waveguide bend radius (µm) for the design's active
    /// fabrication process, so an in-canvas bend-handle drag (or its undo/redo command) cannot
    /// shrink a bend below what the process permits. It reads <see cref="ProcessXsection.MinRadiusUm"/> from the optical
    /// cross-sections of the active process' member PDKs and returns the smallest positive value.
    /// Mirrors the <see cref="MetalRoutingSpecFactory"/> / <see cref="MetalTraceStyleResolver"/>
    /// pattern of turning an <see cref="ActiveProcessSelection"/> plus the loaded PDK drafts into
    /// a routing parameter. When no process is resolvable (playground / no selection / no optical
    /// minimum declared) it falls back to <see cref="BendRadiusEditor.MinRadiusMicrometers"/>,
    /// preserving the previous absolute-minimum behaviour.
    /// </summary>
    public static class WaveguideBendRadiusResolver
    {
        /// <summary>
        /// Resolves the minimum waveguide bend radius from the active process selection and the
        /// loaded PDK drafts. Null selection/drafts or playground mode yields the fallback.
        /// </summary>
        /// <param name="active">The design's active process selection, or null when unset.</param>
        /// <param name="loadedPdks">All currently loaded PDK drafts.</param>
        /// <param name="effectiveMemberPdkNames">
        /// When non-null, REPLACES <see cref="ActiveProcessSelection.MemberPdkNames"/> as the
        /// member filter — pass the live by-value member set (see
        /// <c>LeftPanelViewModel.ResolveLiveMemberPdkNames</c>) so a value-compatible custom PDK
        /// registered after the snapshot was persisted still contributes its minimum bend radius.
        /// Null keeps the snapshot-only lookup.
        /// </param>
        /// <returns>The smallest positive optical minimum bend radius, or the fallback.</returns>
        public static double Resolve(
            ActiveProcessSelection? active, IReadOnlyList<PdkDraft>? loadedPdks,
            IReadOnlyCollection<string>? effectiveMemberPdkNames = null)
        {
            if (active == null || active.IsPlayground || loadedPdks == null)
                return BendRadiusEditor.MinRadiusMicrometers;

            var memberNames = effectiveMemberPdkNames ?? (IEnumerable<string>)active.MemberPdkNames;
            var definitions = memberNames
                .Select(name => loadedPdks.FirstOrDefault(
                    d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.Process)
                .Where(p => p != null)
                .Select(p => p!);

            return Resolve(definitions);
        }

        /// <summary>
        /// Resolves the minimum waveguide bend radius from process definitions directly: the
        /// smallest positive <see cref="ProcessXsection.MinRadiusUm"/> among their optical
        /// cross-sections. Returns <see cref="BendRadiusEditor.MinRadiusMicrometers"/> when none
        /// declares a positive optical minimum.
        /// </summary>
        /// <param name="processes">Process definitions to inspect; null entries are ignored.</param>
        public static double Resolve(IEnumerable<ProcessDefinition?>? processes)
        {
            if (processes == null)
                return BendRadiusEditor.MinRadiusMicrometers;

            double? smallest = null;
            foreach (var process in processes)
            {
                var xsections = process?.Xsections;
                if (xsections == null)
                    continue;

                foreach (var xsection in xsections)
                {
                    if (xsection.Kind != XsectionKind.Optical || xsection.MinRadiusUm <= 0)
                        continue;
                    if (smallest == null || xsection.MinRadiusUm < smallest.Value)
                        smallest = xsection.MinRadiusUm;
                }
            }

            return smallest ?? BendRadiusEditor.MinRadiusMicrometers;
        }
    }
}
