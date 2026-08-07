namespace CAP_Core.Analysis.CircuitOptimization
{
    /// <summary>
    /// Configuration for one circuit optimization run: search space, objective,
    /// evaluation budget and reproducibility seed.
    /// </summary>
    public class OptimizationSettings
    {
        /// <summary>Default number of top variants reported back to the user.</summary>
        public const int DefaultTopN = 5;

        /// <summary>Fraction of a parameter's range used as initial mutation step.</summary>
        public const double InitialStepFraction = 0.25;

        /// <summary>Multiplier applied to the step size after each non-improving move.</summary>
        public const double StepDecayFactor = 0.97;

        /// <summary>Smallest step fraction — keeps the search from freezing completely.</summary>
        public const double MinStepFraction = 0.01;

        /// <summary>The tunable parameters spanning the search space.</summary>
        public IReadOnlyList<OptimizationParameter> Parameters { get; }

        /// <summary>The figure of merit to improve (higher is better).</summary>
        public IOptimizationObjective Objective { get; }

        /// <summary>Laser wavelength in nm used for every evaluation.</summary>
        public int WavelengthNm { get; }

        /// <summary>Maximum number of simulator evaluations (including the baseline).</summary>
        public int EvaluationBudget { get; }

        /// <summary>Random seed so a run is reproducible.</summary>
        public int Seed { get; }

        /// <summary>How many best variants are reported.</summary>
        public int TopN { get; }

        /// <summary>Creates the settings, validating budget and search space.</summary>
        public OptimizationSettings(
            IReadOnlyList<OptimizationParameter> parameters,
            IOptimizationObjective objective,
            int wavelengthNm,
            int evaluationBudget,
            int seed,
            int topN = DefaultTopN)
        {
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            Objective = objective ?? throw new ArgumentNullException(nameof(objective));

            if (parameters.Count == 0)
                throw new ArgumentException("At least one parameter is required.", nameof(parameters));
            if (wavelengthNm <= 0)
                throw new ArgumentOutOfRangeException(nameof(wavelengthNm), "Wavelength must be positive.");
            if (evaluationBudget < 2)
                throw new ArgumentOutOfRangeException(nameof(evaluationBudget), "Budget must allow baseline + at least one trial.");
            if (topN < 1)
                throw new ArgumentOutOfRangeException(nameof(topN), "TopN must be at least 1.");

            WavelengthNm = wavelengthNm;
            EvaluationBudget = evaluationBudget;
            Seed = seed;
            TopN = topN;
        }
    }
}
