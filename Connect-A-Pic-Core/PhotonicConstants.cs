
namespace CAP_Core
{
    public static class PhotonicConstants
    {
        // Aus TestPDK
        public const double GridSizeMicrometers = 250.0;
        public const double StandardWaveguideWidthMicrometers = 1.2;
        public const double StandardBendRadiusMicrometers = 80.0;
        public const double StandardCouplerSpacingMicrometers = 0.35;

        /// <summary>
        /// Conservative default for the minimum edge-to-edge spacing between waveguides
        /// when the active PDK does not declare a process-specific value.
        /// </summary>
        public const double DefaultMinWaveguideSpacingMicrometers = 2.0;

        // Typische Komponenten-Größen
        public const double TypicalMMIWidthMicrometers = 6.0;
        public const double TypicalMMILengthMicrometers = 50.0;
        public const double TypicalGratingCouplerSizeMicrometers = 10.0;

        // Fiber Array Pitch (Standard)
        public const double StandardFiberArrayPitchMicrometers = 250.0; // oder 127 µm
    }
}
