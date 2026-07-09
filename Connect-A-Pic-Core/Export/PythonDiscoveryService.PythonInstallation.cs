namespace CAP_Core.Export;

public partial class PythonDiscoveryService
{
    /// <summary>
    /// Information about a discovered Python installation.
    /// </summary>
    public class PythonInstallation
    {
        /// <summary>
        /// Path to the Python executable.
        /// </summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>
        /// Source/origin of this installation (e.g., "System", "venv: nazca", "Active venv").
        /// </summary>
        public string Source { get; init; } = string.Empty;

        /// <summary>
        /// Python version string (e.g., "3.12.3").
        /// </summary>
        public string? PythonVersion { get; init; }

        /// <summary>
        /// Nazca version string (e.g., "0.6.1"), null if Nazca not installed.
        /// </summary>
        public string? NazcaVersion { get; init; }

        /// <summary>
        /// gdsfactory version string (e.g., "9.5.3"), null if gdsfactory not installed.
        /// </summary>
        public string? GdsFactoryVersion { get; init; }

        /// <summary>
        /// True if this Python has Nazca installed.
        /// </summary>
        public bool HasNazca => !string.IsNullOrEmpty(NazcaVersion);

        /// <summary>
        /// True if this Python has gdsfactory installed.
        /// </summary>
        public bool HasGdsFactory => !string.IsNullOrEmpty(GdsFactoryVersion);

        /// <summary>
        /// Display text for UI (e.g., "System Python 3.12 (Nazca 0.6.1, gdsfactory 9.5.3)").
        /// Both package slots are always shown — "not installed" when absent — so the user
        /// can tell Nazca-only from gdsfactory-only interpreters at a glance (issue #645).
        /// </summary>
        public string DisplayText
        {
            get
            {
                var text = $"{Source}";
                if (PythonVersion != null)
                    text += $" Python {PythonVersion}";
                var nazca = NazcaVersion != null ? $"Nazca {NazcaVersion}" : "Nazca not installed";
                var gds = GdsFactoryVersion != null ? $"gdsfactory {GdsFactoryVersion}" : "gdsfactory not installed";
                text += $" ({nazca}, {gds})";
                return text;
            }
        }
    }
}
