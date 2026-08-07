using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.MonteCarloAnalysis
{
    /// <summary>
    /// Applies Gaussian fabrication jitter to a set of component sliders and
    /// restores their nominal values afterwards. The sigma is relative to each
    /// slider's range, so a 1 % sigma means "1 % of (Max − Min)" per parameter.
    /// </summary>
    public class SliderJitter
    {
        private readonly IReadOnlyList<Slider> _sliders;
        private readonly double[] _nominalValues;

        /// <summary>The nominal (pre-jitter) value of each slider, in slider order.</summary>
        public IReadOnlyList<double> NominalValues => _nominalValues;

        /// <summary>Captures the nominal values of <paramref name="sliders"/> as the jitter baseline.</summary>
        public SliderJitter(IReadOnlyList<Slider> sliders)
        {
            _sliders = sliders ?? throw new ArgumentNullException(nameof(sliders));
            _nominalValues = sliders.Select(s => s.Value).ToArray();
        }

        /// <summary>
        /// Sets every slider to nominal + N(0, σ)·range, clamped to the slider's
        /// [Min, Max] bounds so no physically impossible value is simulated.
        /// </summary>
        public void ApplyJitter(GaussianSampler sampler, double sigmaRelative)
        {
            if (sampler == null) throw new ArgumentNullException(nameof(sampler));

            for (int i = 0; i < _sliders.Count; i++)
            {
                var slider = _sliders[i];
                double range = slider.MaxValue - slider.MinValue;
                double jittered = _nominalValues[i] + sampler.NextGaussian() * sigmaRelative * range;
                slider.Value = Math.Clamp(jittered, slider.MinValue, slider.MaxValue);
            }
        }

        /// <summary>Restores every slider to its captured nominal value.</summary>
        public void RestoreNominal()
        {
            for (int i = 0; i < _sliders.Count; i++)
                _sliders[i].Value = _nominalValues[i];
        }
    }
}
