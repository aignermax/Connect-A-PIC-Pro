namespace CAP_Core.Export.PdkResolution;

/// <summary>
/// Resolution status of a single <c>nazcaFunction</c> string against the
/// installed Python packages (issue #515).
/// </summary>
public enum PdkResolutionStatus
{
    /// <summary>The function resolves cleanly (callable, fixed-cell GDS, static cell, or PCell).</summary>
    Ok,

    /// <summary>The function resolves but with a caveat (e.g. attribute is not callable).</summary>
    Warning,

    /// <summary>Dead reference — the function is not found in the installed Python packages.</summary>
    Error
}

/// <summary>
/// One (module, function) pair to verify. The module is the Python module
/// Lunima would import for rendering/export (already mapped from the raw
/// <c>nazcaFunction</c> string by <see cref="NazcaFunctionPath"/>).
/// </summary>
public class PdkResolutionEntry
{
    /// <summary>Display name of the component the entry belongs to.</summary>
    public string Name { get; init; } = "";

    /// <summary>Python module path (e.g. "demo", "siepic_ebeam_pdk").</summary>
    public string Module { get; init; } = "";

    /// <summary>Bare function/cell name to look up in the module.</summary>
    public string Function { get; init; } = "";
}

/// <summary>Per-entry outcome returned by the resolution helper script.</summary>
public class PdkResolutionResult
{
    /// <summary>Component name, echoed back from the request entry.</summary>
    public string Name { get; init; } = "";

    /// <summary>Resolution status.</summary>
    public PdkResolutionStatus Status { get; init; }

    /// <summary>How the name resolved: "callable", "fixed-cell", "static-cell", "pcell", "attribute", or "".</summary>
    public string Kind { get; init; } = "";

    /// <summary>Human-readable detail (resolution path or error text).</summary>
    public string Message { get; init; } = "";
}

/// <summary>Result of a batch resolution run.</summary>
public class PdkResolutionReport
{
    /// <summary>True when the helper script ran and produced per-entry results.</summary>
    public bool Success { get; init; }

    /// <summary>Run-level error when <see cref="Success"/> is false (e.g. Python missing).</summary>
    public string? Error { get; init; }

    /// <summary>Per-entry results, in request order.</summary>
    public IReadOnlyList<PdkResolutionResult> Results { get; init; } = Array.Empty<PdkResolutionResult>();

    /// <summary>Returns a failure report with the given error message.</summary>
    public static PdkResolutionReport Fail(string error) => new() { Success = false, Error = error };
}
