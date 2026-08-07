using CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance;

namespace CAP_Core.Analysis.MonteCarloAnalysis
{
    /// <summary>
    /// Progress of a Monte-Carlo run: how many of the varied runs finished.
    /// </summary>
    /// <param name="CompletedRuns">Number of varied runs completed so far.</param>
    /// <param name="TotalRuns">Total number of varied runs requested.</param>
    public record MonteCarloProgress(int CompletedRuns, int TotalRuns);

    /// <summary>
    /// Executes a Monte-Carlo fabrication-variance analysis: first the nominal
    /// curve, then N runs with a fresh variance sample drawn from the given
    /// <see cref="IVarianceSource"/> (correlated wafer-level
    /// Δwidth/Δthickness perturbing the component S-matrices). The metric is a
    /// delegate so the same runner serves spectrum envelopes, eye-openness
    /// distributions, or any other per-run curve. Runs are sequential because
    /// the variance sample is shared state read during simulation; the sampler
    /// delegate may offload its own computation to worker threads. The source
    /// is always restored to nominal, even on cancel or error.
    /// </summary>
    public class MonteCarloRunner
    {
        /// <summary>
        /// Runs the analysis.
        /// </summary>
        /// <param name="configuration">Run count and seed.</param>
        /// <param name="varianceSource">Draws and activates one fabrication sample per run.</param>
        /// <param name="sampleCurveAsync">Simulates the design in its current state and returns the metric curve.</param>
        /// <param name="progress">Optional per-run progress sink.</param>
        /// <param name="cancellationToken">Cancels between runs.</param>
        public async Task<MonteCarloResult> RunAsync(
            MonteCarloConfiguration configuration,
            IVarianceSource varianceSource,
            Func<CancellationToken, Task<double[]>> sampleCurveAsync,
            IProgress<MonteCarloProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (varianceSource == null) throw new ArgumentNullException(nameof(varianceSource));
            if (sampleCurveAsync == null) throw new ArgumentNullException(nameof(sampleCurveAsync));

            var sampler = new GaussianSampler(configuration.Seed);
            var runCurves = new List<IReadOnlyList<double>>(configuration.RunCount);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                double[] nominalCurve = await sampleCurveAsync(cancellationToken);

                for (int run = 0; run < configuration.RunCount; run++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    varianceSource.ApplyVariance(sampler);
                    runCurves.Add(await sampleCurveAsync(cancellationToken));

                    progress?.Report(new MonteCarloProgress(run + 1, configuration.RunCount));
                }

                return new MonteCarloResult(nominalCurve, runCurves);
            }
            finally
            {
                varianceSource.RestoreNominal();
            }
        }
    }
}
