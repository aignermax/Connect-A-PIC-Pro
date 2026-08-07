using System;

namespace CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance
{
    /// <summary>
    /// The multiplicative S-matrix perturbation one component receives in one Monte-Carlo
    /// run. Amplitude ≤ 1 (loss only) and phase preserve passivity by
    /// construction; imbalance and wavelength shift are clamped where they are applied.
    /// </summary>
    /// <param name="AmplitudeFactor">Field amplitude factor (≤ 1) from excess loss.</param>
    /// <param name="PhaseRadians">Phase error added to every transmission path.</param>
    /// <param name="WavelengthShiftNm">Spectral shift of the component response (couplers).</param>
    /// <param name="ImbalanceFraction">Signed amplitude asymmetry between output ports (MMIs).</param>
    public sealed record SMatrixPerturbation(
        double AmplitudeFactor,
        double PhaseRadians,
        double WavelengthShiftNm,
        double ImbalanceFraction)
    {
        /// <summary>The identity perturbation (nominal fabrication).</summary>
        public static SMatrixPerturbation None { get; } = new(1.0, 0.0, 0.0, 0.0);
    }

    /// <summary>
    /// Maps a sampled cross-section deviation (Δwidth/Δthickness) to the physical S-matrix
    /// perturbation of one component: Δn_eff → phase error ∝ length, excess
    /// loss per kind, MMI imbalance, coupler spectral shift. Sensitivities are typical
    /// values for SOI strip waveguides (≈480×220 nm, TE, C-band), e.g. Bogaerts et al.,
    /// "Silicon Photonics Circuit Design", Laser Photonics Rev. 2018.
    /// </summary>
    public static class VarianceSensitivityModel
    {
        /// <summary>∂n_eff/∂width for an SOI strip waveguide, per nm.</summary>
        public const double NEffPerNmWidth = 0.002;

        /// <summary>∂n_eff/∂thickness for an SOI strip waveguide, per nm.</summary>
        public const double NEffPerNmThickness = 0.004;

        /// <summary>Reference length the loss sensitivities are normalized to (one grid tile).</summary>
        public const double ReferenceLengthUm = 250;

        /// <summary>Excess loss of a straight/generic section, dB per nm deviation per reference length.</summary>
        public const double StraightLossDbPerNm = 0.002;

        /// <summary>Excess loss of a bend (mode mismatch + sidewall), dB per nm deviation per reference length.</summary>
        public const double BendLossDbPerNm = 0.010;

        /// <summary>Excess loss of an MMI (self-imaging degradation), dB per nm deviation per reference length.</summary>
        public const double MmiLossDbPerNm = 0.008;

        /// <summary>Excess loss of a coupler, dB per nm deviation per reference length.</summary>
        public const double CouplerLossDbPerNm = 0.005;

        /// <summary>Thickness deviations contribute about half as much excess loss as width deviations.</summary>
        public const double ThicknessLossWeight = 0.5;

        /// <summary>MMI output-port amplitude imbalance per nm of width deviation (signed).</summary>
        public const double MmiImbalancePerNmWidth = 0.003;

        /// <summary>MMIs accumulate phase error faster than a strip guide (multimode self-imaging).</summary>
        public const double MmiPhaseErrorFactor = 1.5;

        /// <summary>Coupler spectral shift per nm of width deviation, nm/nm.</summary>
        public const double CouplerShiftNmPerNmWidth = 0.8;

        /// <summary>Coupler spectral shift per nm of thickness deviation, nm/nm.</summary>
        public const double CouplerShiftNmPerNmThickness = 2.0;

        private const double DbPerFieldAmplitudeDecade = 20.0;

        /// <summary>
        /// Computes the perturbation for one component in one run.
        /// </summary>
        /// <param name="kind">Physical classification of the component.</param>
        /// <param name="deviation">Sampled Δwidth/Δthickness for this component.</param>
        /// <param name="wavelengthNm">Simulation wavelength (phase error scales with 1/λ).</param>
        /// <param name="lengthUm">Estimated optical path length of the component.</param>
        public static SMatrixPerturbation Compute(
            ComponentVarianceKind kind,
            ComponentDeviation deviation,
            double wavelengthNm,
            double lengthUm)
        {
            double deltaNEff = NEffPerNmWidth * deviation.DeltaWidthNm
                             + NEffPerNmThickness * deviation.DeltaThicknessNm;

            const double NmPerUm = 1000.0;
            double phase = 2.0 * Math.PI * deltaNEff * lengthUm * NmPerUm / wavelengthNm;
            if (kind == ComponentVarianceKind.Mmi)
                phase *= MmiPhaseErrorFactor;

            double weightedDeviationNm = Math.Abs(deviation.DeltaWidthNm)
                + ThicknessLossWeight * Math.Abs(deviation.DeltaThicknessNm);
            double lossDb = LossSensitivityDbPerNm(kind) * weightedDeviationNm
                * (lengthUm / ReferenceLengthUm);
            double amplitude = Math.Pow(10.0, -lossDb / DbPerFieldAmplitudeDecade);

            double imbalance = kind == ComponentVarianceKind.Mmi
                ? MmiImbalancePerNmWidth * deviation.DeltaWidthNm
                : 0.0;

            double shiftNm = kind == ComponentVarianceKind.Coupler
                ? CouplerShiftNmPerNmWidth * deviation.DeltaWidthNm
                  + CouplerShiftNmPerNmThickness * deviation.DeltaThicknessNm
                : 0.0;

            return new SMatrixPerturbation(amplitude, phase, shiftNm, imbalance);
        }

        private static double LossSensitivityDbPerNm(ComponentVarianceKind kind) => kind switch
        {
            ComponentVarianceKind.Bend => BendLossDbPerNm,
            ComponentVarianceKind.Mmi => MmiLossDbPerNm,
            ComponentVarianceKind.Coupler => CouplerLossDbPerNm,
            _ => StraightLossDbPerNm,
        };
    }
}
