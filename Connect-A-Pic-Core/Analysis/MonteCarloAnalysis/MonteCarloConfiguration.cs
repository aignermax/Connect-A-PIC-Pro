namespace CAP_Core.Analysis.MonteCarloAnalysis
{
    /// <summary>
    /// Configuration for a Monte-Carlo fabrication-variance run: how many varied
    /// simulations to execute and which seed makes the run reproducible. The
    /// variance magnitudes themselves live in the PDK process tolerances
    ///, not here.
    /// </summary>
    public class MonteCarloConfiguration
    {
        /// <summary>Default number of varied runs, per fabrication-yield practice.</summary>
        public const int DefaultRunCount = 1000;

        /// <summary>Default random seed so two identical runs give identical results.</summary>
        public const int DefaultSeed = 42;

        /// <summary>Number of varied simulation runs (the nominal run is extra).</summary>
        public int RunCount { get; }

        /// <summary>Seed for the random source; a fixed seed reproduces identical samples.</summary>
        public int Seed { get; }

        /// <summary>Creates a validated Monte-Carlo configuration.</summary>
        public MonteCarloConfiguration(
            int runCount = DefaultRunCount,
            int seed = DefaultSeed)
        {
            if (runCount < 1)
                throw new ArgumentOutOfRangeException(nameof(runCount), "At least one run is required.");

            RunCount = runCount;
            Seed = seed;
        }
    }
}
