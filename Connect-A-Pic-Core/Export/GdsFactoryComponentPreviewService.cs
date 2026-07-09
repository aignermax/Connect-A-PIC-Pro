namespace CAP_Core.Export;

/// <summary>
/// Geometry-preview service for gdsfactory override code (issue #637). Behaves exactly
/// like <see cref="NazcaComponentPreviewService"/> — same subprocess plumbing, timeout,
/// caching and JSON parsing — but points at <c>render_gdsfactory_preview.py</c>, which
/// speaks the same <c>--code-file</c> CLI and emits the same result contract. A distinct
/// type so DI can register and resolve it alongside the Nazca preview service.
/// Only raw-code mode (<see cref="NazcaComponentPreviewService.RenderRawCodeAsync"/>) is
/// used for gdsfactory; there is no PDK "module mode".
/// </summary>
public sealed class GdsFactoryComponentPreviewService : NazcaComponentPreviewService
{
    /// <summary>Initializes the gdsfactory preview service.</summary>
    /// <param name="pythonExecutable">Interpreter that has gdsfactory installed.</param>
    /// <param name="scriptPath">Path to <c>render_gdsfactory_preview.py</c>.</param>
    /// <param name="timeout">Optional subprocess timeout.</param>
    /// <param name="launchFactory">Shared cross-platform launch factory.</param>
    public GdsFactoryComponentPreviewService(
        string pythonExecutable, string scriptPath, TimeSpan? timeout = null,
        ProcessLaunchFactory? launchFactory = null)
        : base(pythonExecutable, scriptPath, timeout, launchFactory)
    {
    }
}
