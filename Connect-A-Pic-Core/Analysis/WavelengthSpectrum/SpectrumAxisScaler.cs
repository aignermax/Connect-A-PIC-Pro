namespace CAP_Core.Analysis.WavelengthSpectrum
{
    /// <summary>
    /// Computes sensible axis ranges and tick steps for the transmission
    /// spectrum plot so wavelength ticks land on round physical values
    /// (1-2-5 sequence) and the transmission axis always starts at zero.
    /// </summary>
    public static class SpectrumAxisScaler
    {
        /// <summary>Default number of major ticks to aim for on an axis.</summary>
        public const int DefaultTargetTickCount = 8;

        /// <summary>Headroom added above the highest transmission value (fraction of it).</summary>
        public const double TransmissionPaddingFraction = 0.05;

        /// <summary>
        /// Returns a "nice" major tick step (1, 2 or 5 × 10ⁿ) so that a range of
        /// <paramref name="min"/>…<paramref name="max"/> gets close to
        /// <paramref name="targetTickCount"/> major ticks.
        /// </summary>
        /// <param name="min">Axis minimum.</param>
        /// <param name="max">Axis maximum (must exceed <paramref name="min"/>).</param>
        /// <param name="targetTickCount">Desired approximate tick count (≥ 2).</param>
        public static double NiceTickStep(double min, double max, int targetTickCount = DefaultTargetTickCount)
        {
            if (max <= min)
                throw new ArgumentException("max must be greater than min.", nameof(max));
            if (targetTickCount < 2)
                throw new ArgumentOutOfRangeException(nameof(targetTickCount), "Need at least 2 ticks.");

            double rawStep = (max - min) / targetTickCount;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double normalized = rawStep / magnitude; // 1 ≤ normalized < 10

            double niceFactor = normalized switch
            {
                <= 1.0 => 1.0,
                <= 2.0 => 2.0,
                <= 5.0 => 5.0,
                _ => 10.0,
            };
            return niceFactor * magnitude;
        }

        /// <summary>
        /// Returns the transmission-axis maximum: the highest curve value plus
        /// <see cref="TransmissionPaddingFraction"/> headroom, but never below a
        /// visible minimum of 0.01 so an all-dark sweep still shows a scaled axis.
        /// The axis minimum is always 0 (transmission is a power ratio).
        /// </summary>
        /// <param name="curves">The curves that will be plotted.</param>
        public static double TransmissionAxisMax(IEnumerable<TransmissionCurve> curves)
        {
            const double minimumVisibleMax = 0.01;
            double max = 0;
            foreach (var curve in curves)
                foreach (double value in curve.Transmission)
                    max = Math.Max(max, value);

            return Math.Max(max * (1 + TransmissionPaddingFraction), minimumVisibleMax);
        }
    }
}
