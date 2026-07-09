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
        /// Determines whether two physical pins may be connected: both must belong to the same
        /// signal domain. The domain split is electrical vs. everything else — a pin is either
        /// electrical (<see cref="MatterType.Electricity"/>) or optical, where optical covers
        /// both <see cref="MatterType.Light"/> and the <see cref="MatterType.None"/> default of
        /// un-initialized pins. Comparing the raw enum instead would wrongly block a legacy
        /// None pin from connecting to a normal Light pin (#519 review).
        /// </summary>
        public static bool AreKindsCompatible(PhysicalPin first, PhysicalPin second)
            => IsElectrical(first.MatterType) == IsElectrical(second.MatterType);

        /// <summary>True for the electrical domain; everything else (Light, None) is optical.</summary>
        private static bool IsElectrical(MatterType matterType)
            => matterType == MatterType.Electricity;

        /// <summary>
        /// True when the pin carries electrical current (a metal contact), not light. Single
        /// source of the pin-domain check for exporters, which used to re-implement this
        /// individually (<c>SimpleNazcaExporter</c>, <c>GdsFactoryExporter</c>,
        /// <c>GdsFactoryStubWriter</c>) and could drift apart (#682/#686 review).
        /// </summary>
        /// <param name="pin">The physical pin to check; null is treated as not electrical.</param>
        public static bool IsElectrical(PhysicalPin? pin)
            => pin is { MatterType: MatterType.Electricity };

        /// <summary>
        /// User-facing message explaining why two pins of different signal domains cannot be
        /// connected. Single source of the wording, shared by every connect path (both gesture
        /// recognizers and click-to-connect) so the text can't drift between them (#519 review).
        /// </summary>
        public static string DescribeIncompatibility(PhysicalPin first, PhysicalPin second)
            => $"Cannot connect {ToKindName(first.MatterType)} pin {first.Name} "
               + $"to {ToKindName(second.MatterType)} pin {second.Name}";

        /// <summary>
        /// Gets the user-facing kind name ("Optical" / "Electrical") for a matter type.
        /// </summary>
        public static string ToKindName(MatterType matterType)
            => matterType == MatterType.Electricity ? ElectricalKindName : OpticalKindName;
    }
}
