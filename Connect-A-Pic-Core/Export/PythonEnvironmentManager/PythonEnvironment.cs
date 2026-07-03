using System.Runtime.InteropServices;

namespace CAP_Core.Export.PythonEnvironmentManager;

/// <summary>
/// Represents a managed Python virtual environment owned by Lunima.
/// Stores location, status, and detected package versions.
/// </summary>
public class PythonEnvironment
{
    /// <summary>User-facing display name for this environment.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path to the virtual environment root directory.</summary>
    public string VenvPath { get; set; } = string.Empty;

    /// <summary>Current lifecycle or health status.</summary>
    public PythonEnvironmentStatus Status { get; set; } = PythonEnvironmentStatus.Unknown;

    /// <summary>Detected Python version, e.g. "3.11.9". Null before health check.</summary>
    public string? PythonVersion { get; set; }

    /// <summary>Detected Nazca version, e.g. "0.6.1". Null if not installed.</summary>
    public string? NazcaVersion { get; set; }

    /// <summary>Detected gdsfactory version, e.g. "9.5.7". Null if not installed.</summary>
    public string? GdsFactoryVersion { get; set; }

    /// <summary>True when pyclipper is importable in this environment.</summary>
    public bool HasPyclipper { get; set; }

    /// <summary>Last error message from a failed install or health check.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Absolute path to the Python executable inside the venv.
    /// Platform-aware: uses Scripts\python.exe on Windows, bin/python on Unix.
    /// </summary>
    public string PythonExecutable =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(VenvPath, "Scripts", "python.exe")
            : Path.Combine(VenvPath, "bin", "python");

    /// <summary>True when the environment passed its last health check.</summary>
    public bool IsHealthy => Status == PythonEnvironmentStatus.Healthy;
}

/// <summary>Lifecycle and health status of a managed Python environment.</summary>
public enum PythonEnvironmentStatus
{
    /// <summary>Status not yet determined.</summary>
    Unknown,

    /// <summary>Virtual environment directory is being created.</summary>
    Creating,

    /// <summary>Packages (Nazca, pyclipper) are being installed.</summary>
    Installing,

    /// <summary>All required packages are installed and importable.</summary>
    Healthy,

    /// <summary>Installation failed or environment is corrupt — LastError has details.</summary>
    Broken,
}
