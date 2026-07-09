using System.Text.RegularExpressions;

namespace CAP_Core.Components.Core
{
    /// <summary>
    /// Central rules for the TE/TM polarization domain: connection
    /// compatibility, parsing of PDK draft strings, and name-based inference
    /// for legacy PDKs (e.g. SiEPIC's "_TM_" / "tm1550" naming convention).
    /// </summary>
    public static class PolarizationRules
    {
        /// <summary>
        /// Matches a "TM" token in a component or Nazca function name that is
        /// not part of a longer word, e.g. "GC TM 1550", "ebeam_terminator_tm1550".
        /// </summary>
        private static readonly Regex TmNameToken = new(
            @"(?<![a-zA-Z])[tT][mM](?![a-zA-Z])", RegexOptions.Compiled);

        /// <summary>
        /// Determines whether two pins are polarization-compatible for a
        /// waveguide connection. Same-kind connections are allowed, and
        /// <see cref="PolarizationKind.Both"/> connects to anything.
        /// TE↔TM is refused.
        /// </summary>
        public static bool CanConnect(PolarizationKind a, PolarizationKind b)
            => a == b || a == PolarizationKind.Both || b == PolarizationKind.Both;

        /// <summary>
        /// Tries to parse a PDK draft polarization string ("TE", "TM", "Both",
        /// case-insensitive). Null or whitespace parses to the backward-compatible
        /// default <see cref="PolarizationKind.TE"/>.
        /// </summary>
        public static bool TryParse(string? value, out PolarizationKind kind)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                kind = PolarizationKind.TE;
                return true;
            }
            // Enum.TryParse accepts numeric strings ("42") — only the named
            // values TE/TM/Both are valid draft polarizations.
            return Enum.TryParse(value.Trim(), ignoreCase: true, out kind)
                && Enum.IsDefined(kind)
                && !char.IsAsciiDigit(value.TrimStart()[0]);
        }

        /// <summary>
        /// Resolves the effective polarization of a PDK pin: an explicit draft
        /// value wins; otherwise the kind is inferred from the component's
        /// display and Nazca names (SiEPIC-style "TM" token → TM, else TE).
        /// Invalid explicit values fall back to TE — <c>PdkLoader</c> rejects
        /// them at load time before this is reached.
        /// </summary>
        public static PolarizationKind Resolve(string? draftValue, params string?[] componentNames)
        {
            if (!string.IsNullOrWhiteSpace(draftValue))
                return TryParse(draftValue, out var explicitKind) ? explicitKind : PolarizationKind.TE;

            return componentNames.Any(n => n != null && TmNameToken.IsMatch(n))
                ? PolarizationKind.TM
                : PolarizationKind.TE;
        }

        /// <summary>
        /// Builds the user-facing message shown when a connection is refused
        /// because of a TE↔TM mismatch.
        /// </summary>
        public static string GetMismatchMessage(PhysicalPin start, PhysicalPin end)
        {
            return $"Cannot connect {start.Polarization} pin '{start.Name}' to {end.Polarization} pin '{end.Name}' — " +
                   "polarizations are incompatible (use a polarization rotator or splitter).";
        }
    }
}
