using CAP_Core.Analysis.OnaAnalysis;

namespace CAP_Core.Analysis.WavelengthSpectrum
{
    /// <summary>
    /// Maps a <see cref="WavelengthSweepResult"/> to linear transmission curves
    /// (|S|² vs wavelength) — the standard photonics spectrum representation.
    /// Pure data mapping, free of any plotting concerns, so it is unit-testable.
    /// </summary>
    public static class TransmissionSpectrumBuilder
    {
        /// <summary>
        /// Insertion-loss values within this margin (dB) of the −120 dB floor are
        /// treated as "no light" for the noise-floor classification.
        /// </summary>
        public const double NoiseFloorMarginDb = 1.0;

        /// <summary>
        /// Builds one transmission curve per output pin from a sweep result.
        /// Transmission is derived from the per-pin insertion loss:
        /// T = 10^(IL_dB / 10), which is the linear |S|² power ratio.
        /// </summary>
        /// <param name="result">Completed wavelength sweep.</param>
        /// <param name="outputPinFilter">
        ///   Optional set of pin flow-ids to restrict the curves to (the design's
        ///   output couplers). When null — or when no monitored pin matches the
        ///   filter — every monitored pin is mapped, so the caller always gets
        ///   something visible rather than an empty plot.
        /// </param>
        public static IReadOnlyList<TransmissionCurve> Build(
            WavelengthSweepResult result,
            IReadOnlyCollection<Guid>? outputPinFilter = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var pinIds = SelectPinIds(result, outputPinFilter);
            var wavelengths = ToDoubleArray(result.GetWavelengthValues());

            var curves = new List<TransmissionCurve>(pinIds.Count);
            foreach (var pinId in pinIds)
                curves.Add(BuildCurve(result, pinId, wavelengths));
            return curves;
        }

        private static IReadOnlyList<Guid> SelectPinIds(
            WavelengthSweepResult result, IReadOnlyCollection<Guid>? filter)
        {
            if (filter == null || filter.Count == 0)
                return result.MonitoredPinIds;

            var filtered = result.MonitoredPinIds.Where(filter.Contains).ToList();
            return filtered.Count > 0 ? filtered : result.MonitoredPinIds;
        }

        private static TransmissionCurve BuildCurve(
            WavelengthSweepResult result, Guid pinId, double[] wavelengths)
        {
            var lossesDb = result.GetInsertionLossSeriesForPin(pinId);
            var transmission = new double[lossesDb.Length];
            bool atFloor = true;

            for (int i = 0; i < lossesDb.Length; i++)
            {
                transmission[i] = DbToLinear(lossesDb[i]);
                if (lossesDb[i] > WavelengthDataPoint.MinInsertionLossDb + NoiseFloorMarginDb)
                    atFloor = false;
            }

            return new TransmissionCurve(pinId, wavelengths, transmission, atFloor);
        }

        /// <summary>Converts an insertion-loss value in dB to linear power transmission.</summary>
        public static double DbToLinear(double db) => Math.Pow(10.0, db / 10.0);

        private static double[] ToDoubleArray(int[] values)
        {
            var result = new double[values.Length];
            for (int i = 0; i < values.Length; i++)
                result[i] = values[i];
            return result;
        }
    }
}
