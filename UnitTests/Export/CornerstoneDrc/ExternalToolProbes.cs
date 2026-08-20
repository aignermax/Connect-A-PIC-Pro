using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;

namespace UnitTests.Export.CornerstoneDrc;

/// <summary>
/// Environment probes for the Cornerstone DRC tests: locate a usable Python and a
/// KLayout executable, and run external tools through the sanctioned
/// <see cref="ProcessLaunchFactory"/> launch path. Probes return null (never throw)
/// so gated tests can skip cleanly on machines without the tool.
/// </summary>
internal static class ExternalToolProbes
{
    private const int ProbeTimeoutMs = 30_000;
    private const int RunTimeoutMs = 300_000;

    /// <summary>
    /// First Python interpreter answering <c>--version</c> ("python" before "python3",
    /// same order as the nazca-gated tests). Null when none is on PATH; a Windows
    /// Store-alias stub fails the probe naturally (non-zero exit, no output).
    /// </summary>
    public static async Task<string?> FindPythonAsync()
    {
        foreach (var candidate in new[] { "python", "python3" })
        {
            var (exitCode, output, _) = await TryRunAsync(candidate, ProbeTimeoutMs, "--version");
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// KLayout executable for batch DRC: <c>$KLAYOUT</c> first (explicit override),
    /// then <c>klayout</c> / <c>klayout_app</c> on PATH. Null when none answers <c>-v</c>.
    /// </summary>
    public static async Task<string?> FindKlayoutAsync()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KLAYOUT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var (exitCode, _, _) = await TryRunAsync(fromEnv, ProbeTimeoutMs, "-v");
            return exitCode == 0 ? fromEnv : null;
        }

        foreach (var candidate in new[] { "klayout", "klayout_app" })
        {
            var (exitCode, _, _) = await TryRunAsync(candidate, ProbeTimeoutMs, "-v");
            if (exitCode == 0)
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Python that can <c>import cspdk.sin300</c> (the CornerStone SiN export backend):
    /// <c>$CSPDK_PYTHON</c> first, then PATH interpreters, then Lunima managed envs and the
    /// ground-truth venv (same candidates as the gdsfactory script-execution tests, but the
    /// import probe itself decides — no site-packages layout guessing).
    /// </summary>
    public static async Task<string?> FindCspdkPythonAsync()
    {
        var candidates = new List<string>();
        var fromEnv = Environment.GetEnvironmentVariable("CSPDK_PYTHON");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            candidates.Add(fromEnv);
        candidates.Add("python");
        candidates.Add("python3");

        var managedEnvs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        var roots = new List<string> { Path.Combine(Path.GetTempPath(), "gf-groundtruth") };
        if (Directory.Exists(managedEnvs))
            roots.AddRange(Directory.GetDirectories(managedEnvs));
        foreach (var root in roots)
            foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
            {
                var py = Path.Combine(root, rel);
                if (File.Exists(py)) candidates.Add(py);
            }

        foreach (var candidate in candidates.Distinct())
        {
            var (exitCode, _, _) = await TryRunAsync(candidate, ProbeTimeoutMs * 4, "-c", "import cspdk.sin300");
            if (exitCode == 0)
                return candidate;
        }
        return null;
    }

    /// <summary>Runs a tool through the sanctioned factory; a non-launchable tool yields exit -1.</summary>
    public static async Task<(int exitCode, string output, string error)> TryRunAsync(
        string fileName, int timeoutMs, params string[] args)
    {
        try
        {
            return await UvBootstrapper.RunProcessAsync(
                ProcessLaunchFactory.CreateDefault(), fileName, args,
                CancellationToken.None, timeoutMs);
        }
        catch (Exception)
        {
            // A probe must never throw: bare-name launches of a missing tool surface as
            // Win32Exception from Process.Start, an unbuildable one as InvalidOperationException.
            return (-1, string.Empty, "not launchable");
        }
    }

    /// <summary>Runs a tool with the standard 5-minute budget for script/DRC executions.</summary>
    public static Task<(int exitCode, string output, string error)> RunToolAsync(
        string fileName, params string[] args) =>
        TryRunAsync(fileName, RunTimeoutMs, args);
}
