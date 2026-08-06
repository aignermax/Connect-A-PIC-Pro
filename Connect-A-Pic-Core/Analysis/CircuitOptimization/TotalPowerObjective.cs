namespace CAP_Core.Analysis.CircuitOptimization
{
    /// <summary>
    /// Objective that maximizes (or minimizes) the summed optical power over all
    /// monitored pins — a proxy for total transmission / insertion loss.
    /// </summary>
    public class TotalPowerObjective : IOptimizationObjective
    {
        private readonly bool _maximize;

        /// <inheritdoc/>
        public string Name { get; }

        /// <summary>Creates the objective.</summary>
        /// <param name="name">Display name for the ranking.</param>
        /// <param name="maximize">True to maximize total power, false to minimize it.</param>
        public TotalPowerObjective(string name, bool maximize = true)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _maximize = maximize;
        }

        /// <inheritdoc/>
        public double Score(IReadOnlyDictionary<Guid, double> outputPowers)
        {
            double total = outputPowers.Values.Sum();
            return _maximize ? total : -total;
        }
    }
}
