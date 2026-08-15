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
                var declared = DeclaredOpticalMinimum(process);
                if (declared != null && (smallest == null || declared.Value < smallest.Value))
                    smallest = declared;
            }

            return smallest ?? FallbackMinimumMicrometers;
        }

        /// <summary>
        /// Resolves the bend-radius floor for ONE connection from the PDK names of its two
        /// endpoint components (issue #937): each endpoint contributes the optical minimum of
        /// its own PDK's process and the STRICTER of the two governs the whole route — a
        /// cross-process connection must never undercut the tighter chiplet's foundry floor,
        /// and a same-chiplet pair is no longer diluted by a second member PDK's looser
        /// minimum. Endpoints whose PDK is unknown (null name, not loaded, built-in) or whose
        /// process declares no optical minimum contribute nothing; when neither endpoint
        /// resolves, <paramref name="fallback"/> (typically the design-wide
        /// <see cref="Resolve(ActiveProcessSelection, IReadOnlyList{PdkDraft}, IReadOnlyCollection{string}?)"/>
        /// result) applies.
        /// </summary>
        /// <param name="startPdkName">PDK source name of the start pin's component, or null.</param>
        /// <param name="endPdkName">PDK source name of the end pin's component, or null.</param>
        /// <param name="loadedPdks">All currently loaded PDK drafts.</param>
        /// <param name="fallback">Floor to use when neither endpoint resolves a minimum.</param>
        public static double ResolveForEndpointPdks(
            string? startPdkName, string? endPdkName,
            IReadOnlyList<PdkDraft>? loadedPdks, double fallback)
        {
            double? startMinimum = EndpointMinimum(startPdkName, loadedPdks);
            double? endMinimum = EndpointMinimum(endPdkName, loadedPdks);

            if (startMinimum == null) return endMinimum ?? fallback;
            if (endMinimum == null) return startMinimum.Value;
            return Math.Max(startMinimum.Value, endMinimum.Value);
        }

        /// <summary>The declared optical minimum of the named PDK's process, or null.</summary>
        private static double? EndpointMinimum(string? pdkName, IReadOnlyList<PdkDraft>? loadedPdks)
        {
            if (string.IsNullOrEmpty(pdkName) || loadedPdks == null)
                return null;

            var process = loadedPdks.FirstOrDefault(
                d => string.Equals(d.Name, pdkName, StringComparison.OrdinalIgnoreCase))?.Process;
            return DeclaredOpticalMinimum(process);
        }

        /// <summary>
        /// The smallest positive optical <see cref="ProcessXsection.MinRadiusUm"/> of one
        /// process definition, or null when it declares none.
        /// </summary>
        private static double? DeclaredOpticalMinimum(ProcessDefinition? process)
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
