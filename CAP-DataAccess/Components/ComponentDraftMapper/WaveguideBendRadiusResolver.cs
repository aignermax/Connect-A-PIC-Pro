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
    /// minimum declared) it falls back to <see cref="FallbackMinimumMicrometers"/>.
    /// <see cref="ResolveForEndpointPdkNames"/> is the per-connection counterpart (issue #937):
    /// it floors one connection by its endpoint components' own PDK processes instead of the
    /// canvas-wide value.
    /// </summary>
    public static class WaveguideBendRadiusResolver
    {
        /// <summary>
        /// Conservative universal minimum bend radius (µm) used when no process declares one
        /// (playground, no selection, or PDKs without optical minima). Real optical waveguides
        /// cannot bend arbitrarily tightly — 10 µm (the A* router default) keeps even
        /// process-less editing physically plausible instead of allowing sharp corners.
        /// </summary>
        public const double FallbackMinimumMicrometers = 10.0;

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
                return FallbackMinimumMicrometers;

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
        /// cross-sections. Returns <see cref="FallbackMinimumMicrometers"/> when none
        /// declares a positive optical minimum.
        /// </summary>
        /// <param name="processes">Process definitions to inspect; null entries are ignored.</param>
        public static double Resolve(IEnumerable<ProcessDefinition?>? processes)
        {
            if (processes == null)
                return FallbackMinimumMicrometers;

            double? smallest = null;
            foreach (var process in processes)
            {
                var candidate = SmallestOpticalMinimum(process);
                if (candidate != null && (smallest == null || candidate < smallest.Value))
                    smallest = candidate;
            }

            return smallest ?? FallbackMinimumMicrometers;
        }

        /// <summary>
        /// Resolves the per-connection minimum bend radius (µm) from the endpoint components'
        /// PDK sources (issue #937): each endpoint PDK contributes the smallest positive
        /// optical <see cref="ProcessXsection.MinRadiusUm"/> of its own process, and the
        /// STRICTER (larger) of the two governs the connection. On a multi-process canvas
        /// (e.g. a Cornerstone SiN chiplet next to a SiEPIC SOI chiplet) this keeps a
        /// Cornerstone route at its 30 µm foundry floor instead of dragging it down to the
        /// neighbour's 5 µm — and lets a SiEPIC-to-SiEPIC route use its declared 5 µm instead
        /// of the generic fallback. Returns null when neither endpoint PDK resolves to a
        /// positive optical minimum, so the caller keeps the canvas-wide floor.
        /// </summary>
        /// <param name="startPdkName">PDK source name of the start endpoint's component (null = unknown).</param>
        /// <param name="endPdkName">PDK source name of the end endpoint's component (null = unknown).</param>
        /// <param name="loadedPdks">All currently loaded PDK drafts.</param>
        public static double? ResolveForEndpointPdkNames(
            string? startPdkName, string? endPdkName, IReadOnlyList<PdkDraft>? loadedPdks)
        {
            if (loadedPdks == null)
                return null;

            double? start = SmallestOpticalMinimum(FindProcess(startPdkName, loadedPdks));
            double? end = SmallestOpticalMinimum(FindProcess(endPdkName, loadedPdks));
            if (start == null)
                return end;
            if (end == null)
                return start;
            return Math.Max(start.Value, end.Value);
        }

        /// <summary>Finds the process definition of the named loaded PDK (null when unknown).</summary>
        private static ProcessDefinition? FindProcess(string? pdkName, IReadOnlyList<PdkDraft> loadedPdks) =>
            string.IsNullOrEmpty(pdkName)
                ? null
                : loadedPdks.FirstOrDefault(
                    d => string.Equals(d.Name, pdkName, StringComparison.OrdinalIgnoreCase))?.Process;

        /// <summary>
        /// The smallest positive optical <see cref="ProcessXsection.MinRadiusUm"/> of one
        /// process definition, or null when it declares none.
        /// </summary>
        private static double? SmallestOpticalMinimum(ProcessDefinition? process)
        {
            if (process?.Xsections == null)
                return null;

            double? smallest = null;
            foreach (var xsection in process.Xsections)
            {
                if (xsection.Kind != XsectionKind.Optical || xsection.MinRadiusUm <= 0)
                    continue;
                if (smallest == null || xsection.MinRadiusUm < smallest.Value)
                    smallest = xsection.MinRadiusUm;
            }

            return smallest;
        }
    }
}
