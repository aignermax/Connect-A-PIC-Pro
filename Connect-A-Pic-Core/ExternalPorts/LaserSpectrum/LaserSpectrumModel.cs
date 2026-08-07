namespace CAP_Core.ExternalPorts.LaserSpectrum
{
    /// <summary>
    /// Realistic laser source spectrum (Issue #819): a center wavelength with an
    /// optional line shape and FWHM linewidth. The spectrum is discretised into
    /// weighted integer-nanometer samples so it can be superposed through the
    /// existing per-wavelength S-matrix engine — no new solver is required.
    /// An <see cref="LaserLineShape.Ideal"/> shape (or a linewidth of 0) yields a
    /// single full-weight sample, reproducing today's monochromatic behaviour.
    /// </summary>
    public class LaserSpectrumModel
    {
        /// <summary>Default laser relative intensity noise in dB/Hz (typical DFB).</summary>
        public const double DefaultRinDbPerHz = -145;

        /// <summary>The S-matrix engine samples wavelengths on an integer-nm grid.</summary>
        public const int WavelengthResolutionNm = 1;

        /// <summary>Half sampling window in FWHM units for the Gaussian shape (negligible tail beyond).</summary>
        public const double GaussianWindowFwhmFactor = 1.5;

        /// <summary>Half sampling window in FWHM units for the heavy-tailed Lorentzian shape.</summary>
        public const double LorentzianWindowFwhmFactor = 4.0;

        /// <summary>Hard cap of the half sampling window so a huge FWHM cannot explode the run count.</summary>
        public const int MaxHalfWindowNm = 25;

        private const double GaussianExponentFactor = 4 * 0.6931471805599453; // 4·ln2

        /// <summary>Center (peak) wavelength in nanometers.</summary>
        public int CenterWavelengthNm { get; }

        /// <summary>Spectral line shape of the source.</summary>
        public LaserLineShape LineShape { get; }

        /// <summary>Full width at half maximum of the line in nanometers.</summary>
        public double FwhmNm { get; }

        /// <summary>Relative intensity noise in dB/Hz, consumed by the eye-diagram receiver noise model.</summary>
        public double RinDbPerHz { get; }

        /// <summary>Creates a spectrum model; invalid FWHM values fall back to the ideal source.</summary>
        /// <param name="centerWavelengthNm">Center wavelength in nm (must be positive).</param>
        /// <param name="lineShape">Line shape; <see cref="LaserLineShape.Ideal"/> ignores the FWHM.</param>
        /// <param name="fwhmNm">Linewidth (FWHM) in nm; values &lt;= 0 mean ideal.</param>
        /// <param name="rinDbPerHz">Relative intensity noise in dB/Hz.</param>
        public LaserSpectrumModel(
            int centerWavelengthNm,
            LaserLineShape lineShape = LaserLineShape.Ideal,
            double fwhmNm = 0,
            double rinDbPerHz = DefaultRinDbPerHz)
        {
            if (centerWavelengthNm <= 0)
                throw new ArgumentOutOfRangeException(nameof(centerWavelengthNm));
            CenterWavelengthNm = centerWavelengthNm;
            bool isIdeal = lineShape == LaserLineShape.Ideal || fwhmNm <= 0 || double.IsNaN(fwhmNm);
            LineShape = isIdeal ? LaserLineShape.Ideal : lineShape;
            FwhmNm = isIdeal ? 0 : fwhmNm;
            RinDbPerHz = rinDbPerHz;
        }

        /// <summary>True when the spectrum is a single monochromatic sample.</summary>
        public bool IsIdeal => LineShape == LaserLineShape.Ideal;

        /// <summary>
        /// Discretises the spectrum into normalized integer-nm samples (weights sum to 1),
        /// ordered by ascending wavelength. Ideal sources return exactly one sample.
        /// </summary>
        public IReadOnlyList<SpectralSample> GetSamples()
        {
            if (IsIdeal)
                return new[] { new SpectralSample(CenterWavelengthNm, 1.0) };

            int halfWindow = ComputeHalfWindowNm();
            var weights = new List<(int WavelengthNm, double Weight)>();
            for (int offset = -halfWindow; offset <= halfWindow; offset++)
            {
                int wavelength = CenterWavelengthNm + offset * WavelengthResolutionNm;
                if (wavelength <= 0)
                    continue;
                weights.Add((wavelength, ShapeValue(offset)));
            }

            double total = weights.Sum(w => w.Weight);
            return weights
                .Select(w => new SpectralSample(w.WavelengthNm, w.Weight / total))
                .ToList();
        }

        private int ComputeHalfWindowNm()
        {
            double factor = LineShape == LaserLineShape.Lorentzian
                ? LorentzianWindowFwhmFactor
                : GaussianWindowFwhmFactor;
            int halfWindow = (int)Math.Ceiling(factor * FwhmNm);
            return Math.Clamp(halfWindow, 1, MaxHalfWindowNm);
        }

        private double ShapeValue(double detuningNm)
        {
            double relative = detuningNm / FwhmNm;
            return LineShape == LaserLineShape.Lorentzian
                ? 1.0 / (1.0 + 4.0 * relative * relative)
                : Math.Exp(-GaussianExponentFactor * relative * relative);
        }
    }
}
