namespace CAP_Core.Export.PythonEnvironmentManager;

/// <summary>
/// Installs Nazca and its required dependencies (pyclipper) into a managed
/// Python virtual environment using <c>uv pip install</c>.
/// </summary>
public class NazcaPackageInstaller
{
    /// <summary>URL of the Nazca 0.6.1 tarball (no login or licence required).</summary>
    public const string NazcaTarballUrl = "https://nazca-design.org/dist/nazca-0.6.1.tar.gz";

    private readonly ProcessLaunchFactory _launchFactory;

    /// <summary>Initialises the installer.</summary>
    /// <param name="launchFactory">Factory for cross-platform process launches;
    /// null uses <see cref="ProcessLaunchFactory.CreateDefault"/>.</param>
    public NazcaPackageInstaller(ProcessLaunchFactory? launchFactory = null)
    {
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
    }

    /// <summary>
    /// Downloads the Nazca tarball and installs it plus pyclipper into the given venv.
    /// Reports progress; surfaces pip/uv stderr when installation fails (no silent fallback).
    /// </summary>
    /// <param name="uvPath">Absolute path to the uv binary.</param>
    /// <param name="venvPath">Root directory of the target virtual environment.</param>
    /// <param name="progress">Receives human-readable status updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when download fails or pip returns a non-zero exit code.
    /// The exception message contains the installer's stderr output.
    /// </exception>
    public async Task InstallAsync(
        string uvPath,
        string venvPath,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        var tarball = await DownloadNazcaTarballAsync(progress, ct);
        try
        {
            await InstallPackagesAsync(uvPath, venvPath, tarball, progress, ct);
        }
        finally
        {
            TryDeleteTemp(tarball);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static async Task<string> DownloadNazcaTarballAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Downloading Nazca 0.6.1 tarball...");

        var tempPath = Path.Combine(Path.GetTempPath(), "nazca-0.6.1.tar.gz");
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Lunima/1.0");
            await using var stream = await http.GetStreamAsync(NazcaTarballUrl, ct);
            await using var file = File.Create(tempPath);
            await stream.CopyToAsync(file, ct);
            progress?.Report("Nazca tarball downloaded.");
            return tempPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to download Nazca tarball from {NazcaTarballUrl}: {ex.Message}", ex);
        }
    }

    private async Task InstallPackagesAsync(
        string uvPath,
        string venvPath,
        string tarballPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // First: install Nazca from local tarball
        progress?.Report("Installing Nazca into virtual environment...");
        await RunUvPipInstall(uvPath, venvPath, tarballPath, progress, ct);

        // Second: install pyclipper (required by Nazca, not auto-pulled on all platforms)
        progress?.Report("Installing pyclipper...");
        await RunUvPipInstall(uvPath, venvPath, "pyclipper", progress, ct);

        progress?.Report("All packages installed successfully.");
    }

    private async Task RunUvPipInstall(
        string uvPath,
        string venvPath,
        string packageSpec,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var args = new[] { "pip", "install", "--python", GetPythonExe(venvPath), packageSpec };
        var (exitCode, _, stderr) = await UvBootstrapper.RunProcessAsync(
            _launchFactory, uvPath, args, ct, UvBootstrapper.LongOperationTimeoutMs);

        if (exitCode == 0)
            return;

        // Surface stderr to caller — no silent fallback
        var errSummary = string.IsNullOrWhiteSpace(stderr)
            ? $"uv pip install exited with code {exitCode}."
            : $"uv pip install failed (exit {exitCode}):\n{stderr}";

        progress?.Report($"Installation error: {errSummary}");
        throw new InvalidOperationException(errSummary);
    }

    private static string GetPythonExe(string venvPath) =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? Path.Combine(venvPath, "Scripts", "python.exe")
            : Path.Combine(venvPath, "bin", "python");

    private static void TryDeleteTemp(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }
}
