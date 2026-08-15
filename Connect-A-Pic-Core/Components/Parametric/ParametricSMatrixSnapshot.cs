namespace CAP_Core.Components.Parametric
{
    /// <summary>
    /// Immutable description of a parametric S-matrix: the named parameters and the
    /// formula connections from which a live, slider-bound <see cref="LightCalculation.SMatrix"/>
    /// can be rebuilt. Carried on the S-matrix instance so that serializers and clone
    /// operations can recover the formulas — the compiled connection delegates alone do
    /// not expose them. Shared between instances; rebuilding creates fresh per-instance
    /// evaluation state (<see cref="ParametricSMatrix"/>).
    /// </summary>
    public class ParametricSMatrixSnapshot
    {
        /// <summary>
        /// Parameter definitions (names, defaults, ranges, slider bindings).
        /// </summary>
        public IReadOnlyList<ParameterDefinition> Parameters { get; }

        /// <summary>
        /// Formula connections between named pins (magnitude/phase formulas).
        /// </summary>
        public IReadOnlyList<FormulaConnection> Connections { get; }

        /// <summary>
        /// Creates a new snapshot from parameter definitions and formula connections.
        /// </summary>
        public ParametricSMatrixSnapshot(
            IEnumerable<ParameterDefinition> parameters,
            IEnumerable<FormulaConnection> connections)
        {
            Parameters = parameters?.ToList()
                ?? throw new ArgumentNullException(nameof(parameters));
            Connections = connections?.ToList()
                ?? throw new ArgumentNullException(nameof(connections));
        }
    }
}
