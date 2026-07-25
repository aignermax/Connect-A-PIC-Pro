namespace CAP_Core.Solvers.Fdtd;

/// <summary>
/// Machine-readable reason why the FDTD solver backend is unavailable, so UI
/// layers can offer targeted guidance (install vs. start) instead of parsing
/// the human-readable message.
/// </summary>
public enum FdtdUnavailableReason
{
    /// <summary>Backend is available, or the reason is unknown/unspecified.</summary>
    None,

    /// <summary>The backend (Docker) is not installed or not on PATH.</summary>
    NotInstalled,

    /// <summary>The backend is installed but its engine/daemon is not running.</summary>
    EngineNotRunning,

    /// <summary>
    /// The engine is running but the current user may not access it (e.g. Linux:
    /// not in the docker group yet — group membership only applies after
    /// logging out and back in). "Start the engine" would be wrong guidance.
    /// </summary>
    PermissionDenied,

    /// <summary>No API key is configured for a cloud backend (e.g. Tidy3D).</summary>
    MissingApiKey,
}

/// <summary>
/// Result of a quick "can the FDTD solver run here?" probe — checked before the
/// (long) solve so the user gets immediate, actionable feedback instead of a
/// failure deep into the run.
/// </summary>
public class FdtdAvailability
{
    /// <summary>True when the solver backend (e.g. Docker engine) is ready to use.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Human-readable status / how to fix it when not available.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Why the backend is unavailable; <see cref="FdtdUnavailableReason.None"/> when available.</summary>
    public FdtdUnavailableReason Reason { get; init; } = FdtdUnavailableReason.None;

    /// <summary>Creates an available result.</summary>
    public static FdtdAvailability Available(string message) =>
        new() { IsAvailable = true, Message = message };

    /// <summary>Creates an unavailable result with an actionable message.</summary>
    public static FdtdAvailability Unavailable(
        string message, FdtdUnavailableReason reason = FdtdUnavailableReason.None) =>
        new() { IsAvailable = false, Message = message, Reason = reason };
}
