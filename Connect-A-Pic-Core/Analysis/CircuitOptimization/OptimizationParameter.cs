using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.CircuitOptimization
{
    /// <summary>
    /// A tunable degree of freedom for the circuit optimizer: one slider on one
    /// component, bounded by the slider's own min/max range.
    /// </summary>
    public class OptimizationParameter
    {
        /// <summary>The component whose slider is tuned.</summary>
        public Component TargetComponent { get; }

        /// <summary>The slider index on the target component (0-based).</summary>
        public int SliderIndex { get; }

        /// <summary>Human-readable name (e.g. "DC1 · Coupling").</summary>
        public string DisplayName { get; }

        /// <summary>Lower bound of the search range (slider minimum).</summary>
        public double MinValue { get; }

        /// <summary>Upper bound of the search range (slider maximum).</summary>
        public double MaxValue { get; }

        /// <summary>Creates a parameter for the given component slider.</summary>
        public OptimizationParameter(Component targetComponent, int sliderIndex, string displayName)
        {
            TargetComponent = targetComponent ?? throw new ArgumentNullException(nameof(targetComponent));
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));

            if (sliderIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(sliderIndex), "Slider index must be non-negative.");

            var slider = targetComponent.GetSlider(sliderIndex)
                ?? throw new ArgumentException(
                    $"Component '{targetComponent.Identifier}' has no slider at index {sliderIndex}.",
                    nameof(sliderIndex));

            SliderIndex = sliderIndex;
            MinValue = slider.MinValue;
            MaxValue = slider.MaxValue;
        }

        /// <summary>Gets the live slider instance from the target component.</summary>
        public Slider GetSlider() => TargetComponent.GetSlider(SliderIndex)!;

        /// <summary>Clamps a candidate value into the valid slider range.</summary>
        public double Clamp(double value) => Math.Clamp(value, MinValue, MaxValue);
    }
}
