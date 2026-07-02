using CAP_Core.Components.Core;

namespace CAP_Core.Components.PinKinds
{
    /// <summary>
    /// Central helper for the pin-kind (optical vs. electrical) domain distinction.
    /// Parses the JSON <c>pinKind</c> field into <see cref="MatterType"/> values and
    /// checks connection compatibility so that optical pins can never be wired to
    /// electrical pins (Issue #519).
    /// </summary>
    public static class PinKindHelper
    {
        /// <summary>User-facing name of the optical pin kind (JSON value and UI label).</summary>
        public const string OpticalKindName = "Optical";

        /// <summary>User-facing name of the electrical pin kind (JSON value and UI label).</summary>
        public const string ElectricalKindName = "Electrical";

        /// <summary>
        /// Tries to parse a JSON <c>pinKind</c> value into a <see cref="MatterType"/>.
        /// Null, empty, or "Optical" (case-insensitive) map to <see cref="MatterType.Light"/>;
        /// "Electrical" maps to <see cref="MatterType.Electricity"/>. Anything else fails.
        /// </summary>
        /// <param name="pinKind">The raw JSON value; may be null for legacy PDKs.</param>
        /// <param name="matterType">The parsed matter type when parsing succeeds.</param>
        /// <returns>True when the value is a valid (or absent) pin kind.</returns>
        public static bool TryParse(string? pinKind, out MatterType matterType)
        {
            if (string.IsNullOrWhiteSpace(pinKind)
                || string.Equals(pinKind, OpticalKindName, StringComparison.OrdinalIgnoreCase))
            {
                matterType = MatterType.Light;
                return true;
            }
            if (string.Equals(pinKind, ElectricalKindName, StringComparison.OrdinalIgnoreCase))
            {
                matterType = MatterType.Electricity;
                return true;
            }
            matterType = MatterType.None;
            return false;
        }

        /// <summary>
        /// Parses a JSON <c>pinKind</c> value, treating absent values as optical.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the value is not a known pin kind.</exception>
        public static MatterType Parse(string? pinKind)
        {
            if (!TryParse(pinKind, out var matterType))
            {
                throw new ArgumentException(
                    $"Unknown pinKind '{pinKind}'. Expected '{OpticalKindName}' or '{ElectricalKindName}'.",
                    nameof(pinKind));
            }
            return matterType;
        }

        /// <summary>
        /// Determines whether two physical pins may be connected: both must belong
        /// to the same signal domain (optical-to-optical or electrical-to-electrical).
        /// </summary>
        public static bool AreKindsCompatible(PhysicalPin first, PhysicalPin second)
            => first.MatterType == second.MatterType;

        /// <summary>
        /// Gets the user-facing kind name ("Optical" / "Electrical") for a matter type.
        /// </summary>
        public static string ToKindName(MatterType matterType)
            => matterType == MatterType.Electricity ? ElectricalKindName : OpticalKindName;
    }
}
