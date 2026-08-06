namespace CAP_Core.Components.Parametric
{
    /// <summary>
    /// Defines a named parameter for parametric S-Matrix evaluation.
    /// Parameters like "coupling_ratio" or "phase_shift" control
    /// how S-Matrix connection values are computed from formulas.
    /// </summary>
    public class ParameterDefinition
    {
        /// <summary>
        /// Unique name of the parameter (e.g., "coupling_ratio", "phase_shift").
        /// Used as the variable name in formula expressions.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Default value for this parameter.
        /// </summary>
        public double DefaultValue { get; }

        /// <summary>
        /// Minimum allowed value for this parameter.
        /// </summary>
        public double MinValue { get; }

        /// <summary>
        /// Maximum allowed value for this parameter.
        /// </summary>
        public double MaxValue { get; }

        /// <summary>
        /// Display label for UI sliders (defaults to Name if not specified).
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// Slider index (0-based) that controls this parameter, or
        /// <c>null</c> if the parameter has no slider binding. A nullable
        /// int expresses "unbound" distinctly from "index 0".
        /// </summary>
        public int? SliderNumber { get; }

        /// <summary>
        /// Physical unit shown next to the value in the UI (e.g. "dB", "%").
        /// Empty when the parameter is dimensionless.
        /// </summary>
        public string Unit { get; }

        /// <summary>
        /// Creates a new parameter definition.
        /// </summary>
        public ParameterDefinition(
            string name,
            double defaultValue,
            double minValue,
            double maxValue,
            string? label = null,
            int? sliderNumber = null,
            string? unit = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Parameter name cannot be empty.", nameof(name));
            if (minValue > maxValue)
                throw new ArgumentException("MinValue cannot exceed MaxValue.");
            if (defaultValue < minValue || defaultValue > maxValue)
                throw new ArgumentOutOfRangeException(nameof(defaultValue),
                    $"Default value {defaultValue} is outside range [{minValue}, {maxValue}].");
            if (sliderNumber is int sn && sn < 0)
                throw new ArgumentOutOfRangeException(nameof(sliderNumber),
                    $"Slider index {sn} is negative. Use null to mark the parameter as unbound.");

            Name = name;
            DefaultValue = defaultValue;
            MinValue = minValue;
            MaxValue = maxValue;
            Label = label ?? name;
            SliderNumber = sliderNumber;
            Unit = unit ?? string.Empty;
        }
    }
}
