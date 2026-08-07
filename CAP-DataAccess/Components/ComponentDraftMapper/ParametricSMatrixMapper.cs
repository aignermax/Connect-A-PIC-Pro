using CAP_Core.Components.Parametric;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper
{
    /// <summary>
    /// Maps PDK S-Matrix draft data into ParametricSMatrix instances.
    /// Handles both fixed-value and formula-based connections.
    /// </summary>
    public static class ParametricSMatrixMapper
    {
        /// <summary>
        /// Returns true if the S-Matrix draft contains parametric formulas.
        /// </summary>
        public static bool IsParametric(PdkSMatrixDraft draft)
        {
            if (draft == null) return false;
            return draft.Parameters?.Count > 0 ||
                   draft.Connections.Any(c => c.IsParametric);
        }

        /// <summary>
        /// Creates a ParametricSMatrix from a PDK S-Matrix draft.
        /// </summary>
        public static ParametricSMatrix MapToParametricSMatrix(
            PdkSMatrixDraft draft)
        {
            if (draft == null)
                throw new ArgumentNullException(nameof(draft));

            var parameters = MapParameters(draft.Parameters);
            var connections = MapConnections(draft.Connections);

            return new ParametricSMatrix(parameters, connections);
        }

        /// <summary>
        /// Validates a parametric S-Matrix draft for correctness. Throws
        /// <see cref="InvalidOperationException"/> on any defect so bad PDKs
        /// fail at load time, not at simulation time where the failure is
        /// swallowed deep inside the solver.
        /// </summary>
        /// <param name="draft">Parametric S-matrix draft to validate.</param>
        /// <param name="componentName">Used in error messages only.</param>
        /// <param name="pins">All pins on the component (must be non-null).</param>
        /// <param name="sliderCount">
        /// Number of sliders configured on the component. Used to bounds-check
        /// every <see cref="ParameterDefinitionDraft.SliderNumber"/>. Pass 0
        /// if the component has no sliders — parameters referencing a slider
        /// will then correctly be rejected.
        /// </param>
        public static void Validate(
            PdkSMatrixDraft draft,
            string componentName,
            IReadOnlyList<PhysicalPinDraft> pins,
            int sliderCount = 0)
        {
            if (draft.Parameters == null || draft.Parameters.Count == 0)
                return;

            var paramNames = new HashSet<string>();
            foreach (var param in draft.Parameters)
            {
                ValidateParameter(param, componentName, paramNames, sliderCount);
            }

            var pinNames = pins.Select(p => p.Name).ToHashSet();
            ValidateFormulaConnections(
                draft.Connections, componentName, paramNames, pinNames);
        }

        /// <summary>
        /// Maps parameter drafts to core <see cref="ParameterDefinition"/>s.
        /// Public so the template converter can expose the same metadata
        /// (labels, units, ranges) to the properties-panel editor.
        /// </summary>
        public static List<ParameterDefinition> MapParameters(
            List<ParameterDefinitionDraft>? drafts)
        {
            if (drafts == null || drafts.Count == 0)
                return new List<ParameterDefinition>();

            return drafts.Select(d => new ParameterDefinition(
                d.Name, d.DefaultValue, d.MinValue, d.MaxValue, d.Label, d.SliderNumber, d.Unit
            )).ToList();
        }

        private static List<FormulaConnection> MapConnections(
            List<SMatrixConnection> connections)
        {
            var formulaConnections = connections.Select(c =>
            {
                string magFormula = c.MagnitudeFormula
                    ?? c.Magnitude.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                string phaseFormula = c.PhaseDegreesFormula
                    ?? c.PhaseDegrees.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);

                return new FormulaConnection(
                    c.FromPin, c.ToPin, magFormula, phaseFormula);
            }).ToList();

            // Enforce reciprocity: add reverse connections automatically
            return EnforceReciprocity(formulaConnections);
        }

        /// <summary>
        /// Ensures S-Matrix reciprocity by adding reverse connections.
        /// For passive photonic components, S-Matrices must be symmetric (reciprocal).
        /// If a connection A→B exists but B→A doesn't, we add B→A with the same magnitude and phase.
        /// </summary>
        private static List<FormulaConnection> EnforceReciprocity(
            List<FormulaConnection> connections)
        {
            var result = new List<FormulaConnection>(connections);
            var existingPairs = new HashSet<(string, string)>();

            // Track existing connections
            foreach (var conn in connections)
            {
                existingPairs.Add((conn.FromPin, conn.ToPin));
            }

            // Add missing reverse connections
            foreach (var conn in connections)
            {
                var reversePair = (conn.ToPin, conn.FromPin);
                if (!existingPairs.Contains(reversePair))
                {
                    // Add reciprocal connection with same magnitude and phase
                    result.Add(new FormulaConnection(
                        conn.ToPin,
                        conn.FromPin,
                        conn.MagnitudeFormula,
                        conn.PhaseDegFormula));
                    existingPairs.Add(reversePair);
                }
            }

            return result;
        }

        private static void ValidateParameter(
            ParameterDefinitionDraft param,
            string componentName,
            HashSet<string> paramNames,
            int sliderCount)
        {
            if (string.IsNullOrWhiteSpace(param.Name))
            {
                throw new InvalidOperationException(
                    $"Parameter in component '{componentName}' must have a name.");
            }

            if (!paramNames.Add(param.Name))
            {
                throw new InvalidOperationException(
                    $"Duplicate parameter '{param.Name}' in component '{componentName}'.");
            }

            if (param.MinValue > param.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Parameter '{param.Name}' in component '{componentName}' " +
                    $"has minValue > maxValue.");
            }

            // Bounds-check slider index at load time. A bad sliderNumber
            // (missing slider, out-of-range, negative) must not silently
            // leave the parameter unbound — the simulation would then run
            // with the parameter stuck at its default and produce a wrong
            // S-matrix with no warning (CLAUDE.md §10).
            if (param.SliderNumber is int sn)
            {
                if (sn < 0)
                    throw new InvalidOperationException(
                        $"Parameter '{param.Name}' in component '{componentName}' " +
                        $"has negative sliderNumber ({sn}). Omit the field to mark " +
                        $"the parameter as unbound.");
                if (sn >= sliderCount)
                    throw new InvalidOperationException(
                        $"Parameter '{param.Name}' in component '{componentName}' " +
                        $"references sliderNumber {sn}, but the component has only " +
                        $"{sliderCount} slider(s).");
            }
        }

        private static void ValidateFormulaConnections(
            List<SMatrixConnection> connections,
            string componentName,
            HashSet<string> paramNames,
            HashSet<string> pinNames)
        {
            var evaluator = new FormulaEvaluator();

            foreach (var conn in connections)
            {
                if (!pinNames.Contains(conn.FromPin))
                {
                    throw new InvalidOperationException(
                        $"Connection in component '{componentName}' " +
                        $"references unknown pin '{conn.FromPin}'.");
                }
                if (!pinNames.Contains(conn.ToPin))
                {
                    throw new InvalidOperationException(
                        $"Connection in component '{componentName}' " +
                        $"references unknown pin '{conn.ToPin}'.");
                }

                if (!string.IsNullOrWhiteSpace(conn.MagnitudeFormula))
                {
                    if (!evaluator.TryValidate(
                        conn.MagnitudeFormula, paramNames, out string? error))
                    {
                        throw new InvalidOperationException(
                            $"Invalid magnitude formula in component " +
                            $"'{componentName}' ({conn.FromPin}->{conn.ToPin}): {error}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(conn.PhaseDegreesFormula))
                {
                    if (!evaluator.TryValidate(
                        conn.PhaseDegreesFormula, paramNames, out string? error))
                    {
                        throw new InvalidOperationException(
                            $"Invalid phase formula in component " +
                            $"'{componentName}' ({conn.FromPin}->{conn.ToPin}): {error}");
                    }
                }
            }
        }
    }
}
