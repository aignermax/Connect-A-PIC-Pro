using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance
{
    /// <summary>
    /// Correlated wafer-level variance source: every run samples ONE global
    /// Δwidth/Δthickness pair that all same-wafer components share, plus a small
    /// uncorrelated local (within-die) term per component. This replaces i.i.d. per-slider
    /// jitter, which overestimated averaging effects and underestimated systematic corners.
    /// Purely functional — component state is never mutated; the active sample is consumed
    /// by <see cref="PerturbedSystemMatrixBuilder"/> when the system matrix is built.
    /// </summary>
    public sealed class FabricationVarianceSource : IVarianceSource
    {
        /// <summary>Local (within-die) sigma as a fraction of the wafer-level sigma.</summary>
        public const double LocalSigmaFraction = 0.2;

        private readonly IReadOnlyList<Component> _components;
        private readonly Components.Process.ProcessTolerances _tolerances;

        /// <summary>The components participating in the variance analysis.</summary>
        public IReadOnlyList<Component> Components => _components;

        /// <summary>The per-component deviations of the current run; null when nominal.</summary>
        public IReadOnlyDictionary<Component, ComponentDeviation>? CurrentDeviations { get; private set; }

        /// <summary>
        /// Creates a variance source over <paramref name="components"/> using the process
        /// tolerances of the active PDK process.
        /// </summary>
        public FabricationVarianceSource(
            IReadOnlyList<Component> components,
            Components.Process.ProcessTolerances tolerances)
        {
            _components = components ?? throw new ArgumentNullException(nameof(components));
            _tolerances = tolerances ?? throw new ArgumentNullException(nameof(tolerances));
        }

        /// <inheritdoc />
        public void ApplyVariance(GaussianSampler sampler)
        {
            if (sampler == null) throw new ArgumentNullException(nameof(sampler));

            double waferDeltaWidth = sampler.NextGaussian() * _tolerances.WidthSigmaNm;
            double waferDeltaThickness = sampler.NextGaussian() * _tolerances.ThicknessSigmaNm;

            var deviations = new Dictionary<Component, ComponentDeviation>();
            foreach (var component in _components)
            {
                deviations[component] = new ComponentDeviation(
                    waferDeltaWidth + sampler.NextGaussian() * LocalSigmaFraction * _tolerances.WidthSigmaNm,
                    waferDeltaThickness + sampler.NextGaussian() * LocalSigmaFraction * _tolerances.ThicknessSigmaNm);
            }
            CurrentDeviations = deviations;
        }

        /// <inheritdoc />
        public void RestoreNominal() => CurrentDeviations = null;
    }
}
