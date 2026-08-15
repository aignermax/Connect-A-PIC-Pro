using CAP_Core.Export;

namespace CAP_Core.Components.Core
{
    /// <summary>
    /// Represents a physical optical port on a component with µm coordinates.
    /// Used for direct waveguide connections (non-grid mode) and Nazca export.
    /// </summary>
    public class PhysicalPin : ICloneable
    {
        public string Name { get; set; }
        public double OffsetXMicrometers { get; set; }
        public double OffsetYMicrometers { get; set; }
        public double AngleDegrees { get; set; }
        public Guid PinId { get; set; } = Guid.NewGuid();
        public Component ParentComponent { get; set; }

        /// <summary>
        /// Reference to the logical Pin for S-Matrix simulation integration.
        /// When set, waveguide connections through this physical pin will
        /// use the logical pin's IDInFlow/IDOutFlow for light propagation.
        /// </summary>
        public Pin LogicalPin { get; set; }

        /// <summary>
        /// The signal domain of this pin, derived from the linked logical pin.
        /// Pins without a logical pin are treated as optical (the historic default).
        /// Because <see cref="LogicalPin"/> is re-linked after cloning, clones
        /// automatically preserve the kind.
        /// </summary>
        public MatterType MatterType => LogicalPin?.MatterType ?? MatterType.Light;

        /// <summary>
        /// Polarization of this physical port, derived from the linked logical
        /// pin. Physical pins without a logical pin default to the
        /// backward-compatible <see cref="PolarizationKind.TE"/>.
        /// </summary>
        public PolarizationKind Polarization => LogicalPin?.Polarization ?? PolarizationKind.TE;

        /// <summary>
        /// Optional waveguide width in micrometers for this pin, populated from the
        /// component/PDK data when available. Used by DRC to detect width mismatches
        /// between connected pins.
        /// </summary>
        public double? WaveguideWidthMicrometers { get; set; }

        /// <summary>
        /// Optional GDS layer for this pin, populated from the component/PDK data when
        /// available. Used by DRC to detect layer mismatches between connected pins.
        /// </summary>
        public int? Layer { get; set; }

        public (double x, double y) GetAbsolutePosition()
        {
            return (
                ParentComponent.PhysicalX + OffsetXMicrometers,
                ParentComponent.PhysicalY + OffsetYMicrometers
            );
        }

        /// <summary>
        /// Gets the absolute Nazca-coordinate position of this pin: the plain Y negation
        /// of the app-space position (app is Y-down, Nazca is Y-up). The app model is the
        /// truth for where pins are; cell placement is calibrated separately so rendered
        /// pins coincide. Delegates to <see cref="NazcaCoordinateMapper.GetPinNazcaPosition"/>.
        /// </summary>
        public (double x, double y) GetAbsoluteNazcaPosition()
        {
            return NazcaCoordinateMapper.GetPinNazcaPosition(this);
        }

        /// <summary>
        /// Gets the absolute angle of the pin in world-space.
        /// Pin angles are stored relative to the component's local coordinate system.
        /// This method adds the component's rotation to get the world-space angle.
        /// </summary>
        public double GetAbsoluteAngle()
        {
            double absoluteAngle = AngleDegrees + ParentComponent.RotationDegrees;
            // Normalize to 0-360 range
            while (absoluteAngle < 0) absoluteAngle += 360;
            while (absoluteAngle >= 360) absoluteAngle -= 360;
            return absoluteAngle;
        }

        public object Clone()
        {
            return new PhysicalPin
            {
                Name = Name,
                OffsetXMicrometers = OffsetXMicrometers,
                OffsetYMicrometers = OffsetYMicrometers,
                AngleDegrees = AngleDegrees,
                PinId = Guid.NewGuid(),
                WaveguideWidthMicrometers = WaveguideWidthMicrometers,
                Layer = Layer,
                // ParentComponent and LogicalPin are set after cloning by the Component
            };
        }
    }
}
