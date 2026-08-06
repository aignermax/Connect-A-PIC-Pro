namespace CAP_Core.Analysis.MonteCarloAnalysis
{
    /// <summary>
    /// Deterministic standard-normal (μ=0, σ=1) sample source based on a seeded
    /// <see cref="Random"/> and the Box–Muller transform. The same seed always
    /// yields the same sequence, which makes Monte-Carlo runs reproducible.
    /// </summary>
    public class GaussianSampler
    {
        private readonly Random _random;
        private double? _spare;

        /// <summary>Creates a sampler whose sequence is fully determined by <paramref name="seed"/>.</summary>
        public GaussianSampler(int seed)
        {
            _random = new Random(seed);
        }

        /// <summary>Returns the next standard-normal sample of the deterministic sequence.</summary>
        public double NextGaussian()
        {
            if (_spare.HasValue)
            {
                double value = _spare.Value;
                _spare = null;
                return value;
            }

            // Box–Muller: two uniform samples → two independent normal samples.
            double u1 = 1.0 - _random.NextDouble(); // avoid log(0)
            double u2 = _random.NextDouble();
            double radius = Math.Sqrt(-2.0 * Math.Log(u1));
            double angle = 2.0 * Math.PI * u2;

            _spare = radius * Math.Sin(angle);
            return radius * Math.Cos(angle);
        }
    }
}
