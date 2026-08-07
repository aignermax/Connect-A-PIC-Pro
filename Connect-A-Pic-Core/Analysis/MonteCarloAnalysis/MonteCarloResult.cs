namespace CAP_Core.Analysis.MonteCarloAnalysis
{
    /// <summary>
    /// Aggregated result of a Monte-Carlo run: the nominal (unjittered) curve
    /// plus every jittered run's curve, with percentile/envelope statistics.
    /// A "curve" is any fixed-length metric vector — e.g. transmission vs
    /// wavelength, or a single-element eye-openness scalar.
    /// </summary>
    public class MonteCarloResult
    {
        /// <summary>Curve of the unjittered nominal design.</summary>
        public IReadOnlyList<double> NominalCurve { get; }

        /// <summary>One curve per jittered run, all the same length as the nominal curve.</summary>
        public IReadOnlyList<IReadOnlyList<double>> RunCurves { get; }

        /// <summary>Creates a result and validates that all curves have equal length.</summary>
        public MonteCarloResult(
            IReadOnlyList<double> nominalCurve,
            IReadOnlyList<IReadOnlyList<double>> runCurves)
        {
            NominalCurve = nominalCurve ?? throw new ArgumentNullException(nameof(nominalCurve));
            RunCurves = runCurves ?? throw new ArgumentNullException(nameof(runCurves));

            if (runCurves.Any(c => c.Count != nominalCurve.Count))
                throw new ArgumentException("All run curves must match the nominal curve length.", nameof(runCurves));
        }

        /// <summary>Per-index minimum across all jittered runs (lower envelope).</summary>
        public double[] GetMinCurve() => Aggregate(values => values.Min());

        /// <summary>Per-index maximum across all jittered runs (upper envelope).</summary>
        public double[] GetMaxCurve() => Aggregate(values => values.Max());

        /// <summary>Per-index arithmetic mean across all jittered runs.</summary>
        public double[] GetMeanCurve() => Aggregate(values => values.Average());

        /// <summary>
        /// Per-index percentile across all jittered runs, using linear
        /// interpolation between the two nearest order statistics.
        /// </summary>
        /// <param name="percentile">Percentile in [0, 100].</param>
        public double[] GetPercentileCurve(double percentile)
        {
            if (percentile < 0 || percentile > 100)
                throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be within [0, 100].");
            return Aggregate(values => Percentile(values, percentile));
        }

        /// <summary>All jittered-run values at a single curve index (e.g. for a histogram).</summary>
        public double[] GetSamplesAtIndex(int index)
        {
            if (index < 0 || index >= NominalCurve.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return RunCurves.Select(curve => curve[index]).ToArray();
        }

        private double[] Aggregate(Func<double[], double> statistic)
        {
            var result = new double[NominalCurve.Count];
            var column = new double[RunCurves.Count];
            for (int i = 0; i < result.Length; i++)
            {
                for (int run = 0; run < RunCurves.Count; run++)
                    column[run] = RunCurves[run][i];
                result[i] = statistic(column);
            }
            return result;
        }

        private static double Percentile(double[] values, double percentile)
        {
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);

            double rank = percentile / 100.0 * (sorted.Length - 1);
            int lower = (int)Math.Floor(rank);
            int upper = (int)Math.Ceiling(rank);
            if (lower == upper) return sorted[lower];

            double fraction = rank - lower;
            return sorted[lower] + fraction * (sorted[upper] - sorted[lower]);
        }
    }
}
