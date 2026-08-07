namespace CAP_Core.Analysis.CircuitOptimization
{
    /// <summary>
    /// A configurable figure of merit for the circuit optimizer, computed from the
    /// simulated output powers of one evaluation. Higher scores are always better;
    /// implementations that minimize a quantity must negate it.
    /// </summary>
    public interface IOptimizationObjective
    {
        /// <summary>Human-readable name shown in the ranking (e.g. "Power at OUT1").</summary>
        string Name { get; }

        /// <summary>
        /// Computes the score for one simulation run.
        /// </summary>
        /// <param name="outputPowers">Output power (|field|²) per monitored pin GUID.</param>
        double Score(IReadOnlyDictionary<Guid, double> outputPowers);
    }
}
