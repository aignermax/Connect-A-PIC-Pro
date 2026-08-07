using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance
{
    /// <summary>
    /// Coarse physical classification of a component for fabrication-variance modelling
    ///. Each kind maps to different loss/phase/imbalance sensitivities in
    /// <see cref="VarianceSensitivityModel"/>.
    /// </summary>
    public enum ComponentVarianceKind
    {
        /// <summary>Straight propagation section — baseline loss and phase sensitivity.</summary>
        Straight,

        /// <summary>Bend — elevated excess loss under width/thickness deviation (mode mismatch, sidewall).</summary>
        Bend,

        /// <summary>Multimode interferometer — imbalance between output ports plus elevated phase error.</summary>
        Mmi,

        /// <summary>Directional / evanescent coupler — coupling spectrum shifts with the cross-section.</summary>
        Coupler,

        /// <summary>Anything else physical — treated like a straight section.</summary>
        Generic,
    }

    /// <summary>
    /// Derives the <see cref="ComponentVarianceKind"/> from a component's PDK/template
    /// naming. Name-based because Lunima components carry no explicit geometry class;
    /// the fallback (<see cref="ComponentVarianceKind.Generic"/>) still varies, so every
    /// physical component participates in the Monte-Carlo analysis.
    /// </summary>
    public static class ComponentVarianceClassifier
    {
        /// <summary>Classifies <paramref name="component"/> by its factory/template names.</summary>
        public static ComponentVarianceKind Classify(Component component)
        {
            string name = string.Join(
                ' ',
                component.GdsFactoryFunction ?? "",
                component.NazcaFunctionName ?? "",
                component.Name ?? "").ToLowerInvariant();

            if (name.Contains("mmi"))
                return ComponentVarianceKind.Mmi;
            if (name.Contains("bend") || name.Contains("curve") || name.Contains("arc"))
                return ComponentVarianceKind.Bend;
            if (name.Contains("coupler") || name.Contains("splitter") || name.Contains("_dc")
                || name.Contains("directional"))
                return ComponentVarianceKind.Coupler;
            if (name.Contains("straight") || name.Contains("wg") || name.Contains("waveguide"))
                return ComponentVarianceKind.Straight;

            return ComponentVarianceKind.Generic;
        }
    }
}
