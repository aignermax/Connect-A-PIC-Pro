using System.Numerics;

namespace CAP_Core.LightCalculation.LaserSpectrum
{
    /// <summary>
    /// Combines the field results of several per-wavelength S-matrix runs into one
    /// result. Different wavelengths do not interfere, so their optical powers add
    /// incoherently: |E|² = Σ|Eᵢ|². The phase of the strongest contribution is kept
    /// so downstream phase displays remain meaningful for the dominant wavelength.
    /// </summary>
    public static class IncoherentFieldCombiner
    {
        /// <summary>
        /// Combines per-wavelength field dictionaries into a single pin→field map.
        /// A single run is returned unchanged (bit-exact ideal-source behaviour).
        /// </summary>
        /// <param name="perWavelengthFields">Field results of each wavelength run.</param>
        public static Dictionary<Guid, Complex> Combine(
            IReadOnlyList<Dictionary<Guid, Complex>> perWavelengthFields)
        {
            if (perWavelengthFields.Count == 1)
                return perWavelengthFields[0];

            var totalPower = new Dictionary<Guid, double>();
            var dominantField = new Dictionary<Guid, Complex>();
            foreach (var fields in perWavelengthFields)
            {
                foreach (var (pinId, field) in fields)
                {
                    double power = field.Magnitude * field.Magnitude;
                    totalPower[pinId] = totalPower.GetValueOrDefault(pinId) + power;
                    if (!dominantField.TryGetValue(pinId, out var current)
                        || field.Magnitude > current.Magnitude)
                    {
                        dominantField[pinId] = field;
                    }
                }
            }

            var combined = new Dictionary<Guid, Complex>(totalPower.Count);
            foreach (var (pinId, power) in totalPower)
            {
                combined[pinId] = Complex.FromPolarCoordinates(
                    Math.Sqrt(power), dominantField[pinId].Phase);
            }
            return combined;
        }
    }
}
