using CAP_Core.LightCalculation;

namespace CAP_Core.Analysis.CircuitOptimization
{
    /// <summary>
    /// Budget-limited, seeded hill-climb over component slider parameters.
    /// Perturbs one random parameter at a time around the best point found so far,
    /// shrinking the step on non-improving moves, and reports the top-N variants
    /// that beat the baseline design. Slider values are always restored afterwards.
    /// </summary>
    public class CircuitOptimizer
    {
        private readonly ILightCalculator _calculator;

        /// <summary>Creates an optimizer that evaluates designs with the given calculator.</summary>
        public CircuitOptimizer(ILightCalculator calculator)
        {
            _calculator = calculator ?? throw new ArgumentNullException(nameof(calculator));
        }

        /// <summary>
        /// Runs the search. Cancellation stops the run cleanly: partial results are
        /// returned with <see cref="OptimizationResult.WasCancelled"/> set, and the
        /// original slider values are restored either way.
        /// </summary>
        public async Task<OptimizationResult> RunAsync(
            OptimizationSettings settings,
            CancellationToken cancellationToken = default,
            IProgress<OptimizationProgress>? progress = null)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var originalValues = settings.Parameters.Select(p => p.GetSlider().Value).ToArray();
            try
            {
                return await SearchAsync(settings, originalValues, cancellationToken, progress);
            }
            finally
            {
                ApplyValues(settings, originalValues);
            }
        }

        private async Task<OptimizationResult> SearchAsync(
            OptimizationSettings settings,
            double[] startValues,
            CancellationToken cancellationToken,
            IProgress<OptimizationProgress>? progress)
        {
            var rng = new Random(settings.Seed);
            var evaluated = new List<OptimizationCandidate>();
            double stepFraction = OptimizationSettings.InitialStepFraction;
            int evaluations = 0;
            bool cancelled = false;

            double baselineScore = double.NaN;
            var bestValues = (double[])startValues.Clone();
            double bestScore = double.NegativeInfinity;

            try
            {
                baselineScore = await EvaluateAsync(settings, startValues, cancellationToken);
                evaluations++;
                bestScore = baselineScore;
                progress?.Report(new OptimizationProgress(evaluations, settings.EvaluationBudget, bestScore));

                while (evaluations < settings.EvaluationBudget)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var candidateValues = Mutate(settings.Parameters, bestValues, rng, stepFraction);
                    double score = await EvaluateAsync(settings, candidateValues, cancellationToken);
                    evaluations++;
                    evaluated.Add(new OptimizationCandidate(candidateValues, score));

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestValues = candidateValues;
                        stepFraction = OptimizationSettings.InitialStepFraction;
                    }
                    else
                    {
                        stepFraction = Math.Max(
                            OptimizationSettings.MinStepFraction,
                            stepFraction * OptimizationSettings.StepDecayFactor);
                    }

                    progress?.Report(new OptimizationProgress(evaluations, settings.EvaluationBudget, bestScore));
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            var top = SelectTopVariants(evaluated, baselineScore, settings.TopN);
            return new OptimizationResult(settings, baselineScore, top, evaluations, cancelled);
        }

        private async Task<double> EvaluateAsync(
            OptimizationSettings settings,
            double[] values,
            CancellationToken cancellationToken)
        {
            ApplyValues(settings, values);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var fieldResults = await _calculator.CalculateFieldPropagationAsync(cts, settings.WavelengthNm);

            var powers = new Dictionary<Guid, double>(fieldResults.Count);
            foreach (var kvp in fieldResults)
                powers[kvp.Key] = kvp.Value.Magnitude * kvp.Value.Magnitude;

            return settings.Objective.Score(powers);
        }

        private static double[] Mutate(
            IReadOnlyList<OptimizationParameter> parameters,
            double[] baseValues,
            Random rng,
            double stepFraction)
        {
            var values = (double[])baseValues.Clone();
            int index = rng.Next(parameters.Count);
            var parameter = parameters[index];

            double range = parameter.MaxValue - parameter.MinValue;
            double delta = range * stepFraction * (2 * rng.NextDouble() - 1);
            values[index] = parameter.Clamp(values[index] + delta);
            return values;
        }

        private static void ApplyValues(OptimizationSettings settings, double[] values)
        {
            for (int i = 0; i < settings.Parameters.Count; i++)
                settings.Parameters[i].GetSlider().Value = values[i];
        }

        private static List<OptimizationCandidate> SelectTopVariants(
            List<OptimizationCandidate> evaluated,
            double baselineScore,
            int topN)
        {
            if (double.IsNaN(baselineScore))
                return new List<OptimizationCandidate>();

            return evaluated
                .Where(c => c.Score > baselineScore)
                .OrderByDescending(c => c.Score)
                .DistinctBy(c => string.Join(";", c.ParameterValues))
                .Take(topN)
                .ToList();
        }
    }
}
