namespace CAP_Core.Analysis.CircuitOptimization
{
    /// <summary>
    /// One evaluated point in the search space: a full parameter assignment and
    /// the objective score it achieved.
    /// </summary>
    public class OptimizationCandidate
    {
        /// <summary>Parameter values, aligned with <see cref="OptimizationSettings.Parameters"/>.</summary>
        public IReadOnlyList<double> ParameterValues { get; }

        /// <summary>Objective score of this candidate (higher is better).</summary>
        public double Score { get; }

        /// <summary>Creates a candidate from a snapshot of parameter values.</summary>
        public OptimizationCandidate(IReadOnlyList<double> parameterValues, double score)
        {
            ParameterValues = parameterValues ?? throw new ArgumentNullException(nameof(parameterValues));
            Score = score;
        }

        /// <summary>Improvement of this candidate over a baseline score.</summary>
        public double ImprovementOver(double baselineScore) => Score - baselineScore;
    }
}
