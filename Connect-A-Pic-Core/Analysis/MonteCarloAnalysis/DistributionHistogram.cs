namespace CAP_Core.Analysis.MonteCarloAnalysis
{
    /// <summary>
    /// One-dimensional histogram of a Monte-Carlo scalar metric (e.g. eye
    /// openness or transmission at a fixed wavelength), used to visualize how
    /// fabrication jitter spreads the metric around its nominal value.
    /// </summary>
    public class DistributionHistogram
    {
        /// <summary>Default number of histogram bins.</summary>
        public const int DefaultBinCount = 25;

        /// <summary>Lower edge of the first bin.</summary>
        public double MinValue { get; }

        /// <summary>Upper edge of the last bin.</summary>
        public double MaxValue { get; }

        /// <summary>Sample count per bin, in ascending value order.</summary>
        public IReadOnlyList<int> BinCounts { get; }

        /// <summary>Width of each bin.</summary>
        public double BinWidth => BinCounts.Count == 0 ? 0 : (MaxValue - MinValue) / BinCounts.Count;

        private DistributionHistogram(double minValue, double maxValue, int[] binCounts)
        {
            MinValue = minValue;
            MaxValue = maxValue;
            BinCounts = binCounts;
        }

        /// <summary>Center value of the bin at <paramref name="binIndex"/>.</summary>
        public double BinCenter(int binIndex) => MinValue + (binIndex + 0.5) * BinWidth;

        /// <summary>
        /// Builds a histogram over <paramref name="samples"/>. If all samples are
        /// identical, a single fully-populated bin around that value is returned.
        /// </summary>
        public static DistributionHistogram Create(IReadOnlyList<double> samples, int binCount = DefaultBinCount)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (samples.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(samples));
            if (binCount < 1) throw new ArgumentOutOfRangeException(nameof(binCount));

            double min = samples.Min();
            double max = samples.Max();
            if (min == max)
                return new DistributionHistogram(min, max, new[] { samples.Count });

            var counts = new int[binCount];
            double span = max - min;
            foreach (double sample in samples)
            {
                int bin = Math.Min((int)((sample - min) / span * binCount), binCount - 1);
                counts[bin]++;
            }
            return new DistributionHistogram(min, max, counts);
        }
    }
}
