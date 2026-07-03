namespace CAP_Core.Export;

/// <summary>
/// Preview back-end for per-instance gdsfactory raw-code overrides (issue #637).
/// Runs <c>scripts/render_gdsfactory_preview.py</c>, which executes a user-supplied
/// snippet defining <c>component()</c> (returning a <c>gf.Component</c>) and emits
/// the same JSON shape as the Nazca preview script — bbox, polygons and port pins —
/// so all subprocess plumbing, parsing and caching is inherited from
/// <see cref="NazcaComponentPreviewService"/> unchanged.
/// Registered as its own type so DI can hand out the Nazca-script and
/// gdsfactory-script instances side by side.
/// </summary>
public class GdsFactoryComponentPreviewService : NazcaComponentPreviewService
{
    /// <summary>
    /// Initializes the gdsfactory preview service.
    /// </summary>
    /// <param name="pythonExecutable">Python interpreter with gdsfactory installed
    /// (the same one the gdsfactory export runs with).</param>
    /// <param name="scriptPath">Absolute path to render_gdsfactory_preview.py.</param>
    /// <param name="timeout">Optional subprocess timeout.</param>
    /// <param name="launchFactory">Factory used to build process start info.</param>
    public GdsFactoryComponentPreviewService(
        string pythonExecutable,
        string scriptPath,
        TimeSpan? timeout = null,
        ProcessLaunchFactory? launchFactory = null)
        : base(pythonExecutable, scriptPath, timeout, launchFactory)
    {
    }
}
