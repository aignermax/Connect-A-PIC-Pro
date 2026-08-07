namespace CAP_Core.Analysis.CircuitOptimization
{
    /// <summary>
    /// Outcome of a circuit optimization run: the baseline score of the untouched
    /// design and the top-N strictly better variants found within the budget.
    /// </summary>
    public class OptimizationResult
    {
        /// <summary>The settings that produced this result.</summary>
        public OptimizationSettings Settings { get; }

        /// <summary>Objective score of the design as it was before the search.</summary>
        public double BaselineScore { get; }

        /// <summary>
        /// Variants that scored strictly better than the baseline, best first,
        /// at most <see cref="OptimizationSettings.TopN"/> entries.
        /// </summary>
        public IReadOnlyList<OptimizationCandidate> TopVariants { get; }

        /// <summary>Number of simulator evaluations actually spent.</summary>
        public int EvaluationsUsed { get; }

        /// <summary>True when the run was stopped by the user before the budget ran out.</summary>
        public bool WasCancelled { get; }

        /// <summary>Creates a result snapshot.</summary>
        public OptimizationResult(
            OptimizationSettings settings,
            double baselineScore,
            IReadOnlyList<OptimizationCandidate> topVariants,
            int evaluationsUsed,
            bool wasCancelled)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            TopVariants = topVariants ?? throw new ArgumentNullException(nameof(topVariants));
            BaselineScore = baselineScore;
            EvaluationsUsed = evaluationsUsed;
            WasCancelled = wasCancelled;
        }
    }

    /// <summary>Progress snapshot reported after every evaluation.</summary>
    /// <param name="EvaluationsDone">Evaluations spent so far.</param>
    /// <param name="Budget">Total evaluation budget.</param>
    /// <param name="BestScore">Best score seen so far (baseline included).</param>
    public readonly record struct OptimizationProgress(int EvaluationsDone, int Budget, double BestScore);
}
