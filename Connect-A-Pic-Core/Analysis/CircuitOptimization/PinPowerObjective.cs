namespace CAP_Core.Analysis.CircuitOptimization
{
    /// <summary>
    /// Objective that maximizes (or minimizes) the summed optical power over a
    /// chosen set of pins — e.g. one MZI output port, or all output couplers of
    /// the circuit for a transmission target.
    /// </summary>
    public class PinPowerObjective : IOptimizationObjective
    {
        private readonly IReadOnlyCollection<Guid> _pinIds;
        private readonly bool _maximize;

        /// <inheritdoc/>
        public string Name { get; }

        /// <summary>Creates the objective for a set of monitored pins.</summary>
        /// <param name="pinIds">Pin GUIDs whose powers are summed into the score.</param>
        /// <param name="name">Display name for the ranking.</param>
        /// <param name="maximize">True to maximize the power, false to minimize it.</param>
        public PinPowerObjective(IReadOnlyCollection<Guid> pinIds, string name, bool maximize = true)
        {
            _pinIds = pinIds ?? throw new ArgumentNullException(nameof(pinIds));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _maximize = maximize;

            if (pinIds.Count == 0)
                throw new ArgumentException("At least one pin is required.", nameof(pinIds));
        }

        /// <inheritdoc/>
        public double Score(IReadOnlyDictionary<Guid, double> outputPowers)
        {
            double total = 0;
            foreach (var pinId in _pinIds)
            {
                if (outputPowers.TryGetValue(pinId, out double power))
                    total += power;
            }
            return _maximize ? total : -total;
        }
    }
}
