namespace CAP_Core.Analysis.WavelengthSpectrum
{
    /// <summary>
    /// One transmission-vs-wavelength curve of the spectrum plot: the linear
    /// power transmission |S|² (0…1) of a single output pin across the sweep.
    /// </summary>
    public class TransmissionCurve
    {
        /// <summary>Flow-id of the output pin this curve belongs to.</summary>
        public Guid PinId { get; }

        /// <summary>Wavelength values (nm) of the sweep, one per sample.</summary>
        public IReadOnlyList<double> WavelengthsNm { get; }

        /// <summary>Linear power transmission |S|² (0…1), one per wavelength sample.</summary>
        public IReadOnlyList<double> Transmission { get; }

        /// <summary>
        /// True when every sample sits at the sweep's −120 dB noise floor,
        /// i.e. no light ever reached this pin.
        /// </summary>
        public bool IsAtNoiseFloor { get; }

        /// <summary>Creates a transmission curve.</summary>
        /// <param name="pinId">Flow-id of the output pin.</param>
        /// <param name="wavelengthsNm">Wavelength values (nm), one per sample.</param>
        /// <param name="transmission">Linear transmission values, same length as <paramref name="wavelengthsNm"/>.</param>
        /// <param name="isAtNoiseFloor">Whether all samples are at the noise floor.</param>
        public TransmissionCurve(
            Guid pinId,
            IReadOnlyList<double> wavelengthsNm,
            IReadOnlyList<double> transmission,
            bool isAtNoiseFloor)
        {
            if (wavelengthsNm == null) throw new ArgumentNullException(nameof(wavelengthsNm));
            if (transmission == null) throw new ArgumentNullException(nameof(transmission));
            if (wavelengthsNm.Count != transmission.Count)
                throw new ArgumentException("Wavelength and transmission arrays must have equal length.", nameof(transmission));

            PinId = pinId;
            WavelengthsNm = wavelengthsNm;
            Transmission = transmission;
            IsAtNoiseFloor = isAtNoiseFloor;
        }
    }
}
