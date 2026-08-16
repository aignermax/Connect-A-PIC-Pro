using CAP_Core.Analysis;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper
{
    /// <summary>
    /// Resolves the DRC-lite rule set governing ONE waveguide connection from the PDK
    /// sources of its endpoint components (issue #936) — the width/spacing counterpart
    /// of <see cref="WaveguideBendRadiusResolver.ResolveForEndpointPdkNames"/> (issue
    /// #937). On a multi-process canvas (e.g. a Cornerstone SiN chiplet next to a
    /// SiEPIC SOI chiplet) this keys every connection to its own endpoints' process
    /// limits instead of the design-wide rule set taken from the active process' first
    /// member PDK — and it works in Playground, where no process lock exists at all.
    /// Only DECLARED values participate: a PDK without <c>minWidthUm</c> /
    /// <c>minWaveguideSpacingUm</c> contributes nothing (the #926 no-invented-values
    /// rule). For a cross-chiplet connection both endpoint processes contribute — the
    /// stricter side governs, same rule as #937.
    /// </summary>
    public static class ConnectionDrcRuleResolver
    {
        /// <summary>
        /// Resolves the per-connection DRC-lite rules from the endpoint components' PDK
        /// sources: the min-width rules are the union of both endpoint processes'
        /// declared cross-section minimums (the pin-layer association in
        /// <see cref="WaveguideMinWidthChecker"/> picks each side's own rule), the
        /// minimum spacing is the stricter (larger) of the two declared process values.
        /// Returns null when neither endpoint resolves to a loaded PDK process — "no
        /// PDK opinion"; the caller then falls back to its canvas-wide rule set, the
        /// same fallback rule as the #937 bend floor.
        /// </summary>
        /// <param name="startPdkName">PDK source name of the start endpoint's component (null = unknown).</param>
        /// <param name="endPdkName">PDK source name of the end endpoint's component (null = unknown).</param>
        /// <param name="loadedPdks">All currently loaded PDK drafts.</param>
        public static ConnectionDrcRules? ResolveForEndpointPdkNames(
            string? startPdkName, string? endPdkName, IReadOnlyList<PdkDraft>? loadedPdks)
        {
            if (loadedPdks == null)
                return null;

            var startProcess = FindProcess(startPdkName, loadedPdks);
            var endProcess = FindProcess(endPdkName, loadedPdks);
            if (startProcess == null && endProcess == null)
                return null;

            var widthRules = new List<WaveguideMinWidthRule>();
            widthRules.AddRange(startProcess.GetMinWaveguideWidthRules());
            if (!ReferenceEquals(startProcess, endProcess))
                widthRules.AddRange(endProcess.GetMinWaveguideWidthRules());

            double minSpacing = Math.Max(
                startProcess?.MinWaveguideSpacingUm ?? 0,
                endProcess?.MinWaveguideSpacingUm ?? 0);

            return new ConnectionDrcRules(widthRules, minSpacing);
        }

        /// <summary>Finds the process definition of the named loaded PDK (null when unknown).</summary>
        private static ProcessDefinition? FindProcess(string? pdkName, IReadOnlyList<PdkDraft> loadedPdks) =>
            string.IsNullOrEmpty(pdkName)
                ? null
                : loadedPdks.FirstOrDefault(
                    d => string.Equals(d.Name, pdkName, StringComparison.OrdinalIgnoreCase))?.Process;
    }
}
