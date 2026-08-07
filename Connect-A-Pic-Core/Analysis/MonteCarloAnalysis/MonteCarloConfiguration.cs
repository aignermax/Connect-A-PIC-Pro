namespace CAP_Core.Analysis.MonteCarloAnalysis
{
    /// <summary>
    /// Configuration for a Monte-Carlo fabrication-variance run: how many
    /// jittered simulations to execute, how strong the Gaussian parameter
    /// jitter is, and which seed makes the run reproducible.
    /// </summary>
    public class MonteCarloConfiguration
    {
        /// <summary>Default number of jittered runs, per fabrication-yield practice.</summary>
        public const int DefaultRunCount = 1000;

        /// <summary>Default jitter sigma as a fraction of each slider's range (1 %).</summary>
        public const double DefaultSigmaRelative = 0.01;

        /// <summary>Default random seed so two identical runs give identical results.</summary>
        public const int DefaultSeed = 42;

        /// <summary>Number of jittered simulation runs (the nominal run is extra).</summary>
        public int RunCount { get; }

        /// <summary>
        /// Standard deviation of the Gaussian jitter expressed as a fraction of
        /// each parameter's slider range (MaxValue − MinValue).
        /// </summary>
        public double SigmaRelative { get; }

        /// <summary>Seed for the random source; a fixed seed reproduces identical samples.</summary>
        public int Seed { get; }

        /// <summary>Creates a validated Monte-Carlo configuration.</summary>
        public MonteCarloConfiguration(
            int runCount = DefaultRunCount,
            double sigmaRelative = DefaultSigmaRelative,
            int seed = DefaultSeed)
        {
            if (runCount < 1)
                throw new ArgumentOutOfRangeException(nameof(runCount), "At least one run is required.");
            if (sigmaRelative < 0 || !double.IsFinite(sigmaRelative))
                throw new ArgumentOutOfRangeException(nameof(sigmaRelative), "Sigma must be a non-negative finite number.");

            RunCount = runCount;
            SigmaRelative = sigmaRelative;
            Seed = seed;
        }
    }
}
