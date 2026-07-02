namespace CAP_Core.Export.PythonEnvironmentManager;

/// <summary>
/// Checks the health of a managed Python environment by verifying that Python
/// is executable, Nazca is importable, and pyclipper is importable.
/// Reuses <see cref="PythonDiscoveryService.CheckPythonInstallation"/> for
/// Python and Nazca version detection.
/// </summary>
public class EnvironmentHealthChecker
{
    private readonly PythonDiscoveryService _discovery;
    private readonly ProcessLaunchFactory _launchFactory;

    /// <summary>Initialises a new health checker with a shared discovery service.</summary>
    /// <param name="discovery">Discovery service for probing interpreter capabilities.</param>
    /// <param name="launchFactory">Factory for cross-platform process launches;
    /// null uses <see cref="ProcessLaunchFactory.CreateDefault"/>.</param>
    public EnvironmentHealthChecker(PythonDiscoveryService discovery, ProcessLaunchFactory? launchFactory = null)
    {
        _discovery = discovery;
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
    }

    /// <summary>
    /// Probes the given environment and updates its status, version fields,
    /// and <see cref="PythonEnvironment.LastError"/> in place.
    /// </summary>
    /// <param name="env">Environment to check. Modified in place.</param>
    /// <param name="ct">Cancellation token; cancelling aborts the probes.</param>
    /// <returns>The same <paramref name="env"/> instance after updating.</returns>
    public async Task<PythonEnvironment> CheckAsync(PythonEnvironment env, CancellationToken ct = default)
    {
        var pythonExe = env.PythonExecutable;

        if (!File.Exists(pythonExe))
        {
            MarkBroken(env, "Python executable not found at expected path.");
            return env;
        }

        ct.ThrowIfCancellationRequested();
        var installation = await _discovery.CheckPythonInstallation(pythonExe, "Managed");
        if (installation == null)
        {
            MarkBroken(env, "Python executable exists but could not be queried for version.");
            return env;
        }

        env.PythonVersion = installation.PythonVersion;
        env.NazcaVersion = installation.NazcaVersion;

        if (!installation.HasNazca)
        {
            MarkBroken(env, "Nazca is not installed or cannot be imported.");
            return env;
        }

        env.HasPyclipper = await CheckPyclipperAsync(pythonExe, ct);

        env.Status = PythonEnvironmentStatus.Healthy;
        env.LastError = null;
        return env;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static void MarkBroken(PythonEnvironment env, string reason)
    {
        env.Status = PythonEnvironmentStatus.Broken;
        env.LastError = reason;
    }

    private async Task<bool> CheckPyclipperAsync(string pythonPath, CancellationToken ct)
    {
        try
        {
            var (exitCode, _, _) = await UvBootstrapper.RunProcessAsync(
                _launchFactory,
                pythonPath,
                new[] { "-c", "import pyclipper" },
                ct,
                timeoutMs: 10_000);
            return exitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
