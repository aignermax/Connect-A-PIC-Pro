using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CAP_Core.Export.PythonEnvironmentManager;

/// <summary>
/// Locates or downloads the <c>uv</c> binary and uses it to create Python virtual
/// environments with a specific Python version. Supports Windows, Linux, and macOS.
/// All subprocesses are launched through <see cref="ProcessLaunchFactory"/> so PATH
/// resolution behaves identically on every OS (CLAUDE.md §1.2).
/// </summary>
public class UvBootstrapper
{
    /// <summary>Default Python version to request when none is specified.</summary>
    public const string DefaultPythonVersion = "3.11";

    /// <summary>
    /// Timeout for uv operations that may download a full CPython toolchain on first
    /// use — 120s is not enough on slow connections, so these get 10 minutes.
    /// </summary>
    public const int LongOperationTimeoutMs = 600_000;

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima");

    private static readonly string ToolsDir = Path.Combine(AppDataDir, "tools");

    private static readonly string LocalUvExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(ToolsDir, "uv.exe")
        : Path.Combine(ToolsDir, "uv");

    private readonly ProcessLaunchFactory _launchFactory;

    /// <summary>Directory where managed environments are stored.</summary>
    public static string EnvironmentsBaseDir => Path.Combine(AppDataDir, "envs");

    /// <summary>Initialises the bootstrapper.</summary>
    /// <param name="launchFactory">Factory for cross-platform process launches;
    /// null uses <see cref="ProcessLaunchFactory.CreateDefault"/>.</param>
    public UvBootstrapper(ProcessLaunchFactory? launchFactory = null)
    {
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
    }

    /// <summary>
    /// Resolves the path to the <c>uv</c> executable: checks PATH first, then the
    /// local tools directory, then downloads it if not found anywhere.
    /// </summary>
    /// <param name="progress">Receives human-readable status updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to a working <c>uv</c> binary.</returns>
    /// <exception cref="InvalidOperationException">If uv cannot be found or downloaded.</exception>
    public async Task<string> EnsureUvAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var pathUv = FindUvOnPath();
        if (pathUv != null)
        {
            progress?.Report($"Found uv at {pathUv}");
            return pathUv;
        }

        if (File.Exists(LocalUvExe))
        {
            progress?.Report($"Using cached uv at {LocalUvExe}");
            return LocalUvExe;
        }

        progress?.Report("uv not found — downloading...");
        await DownloadUvAsync(progress, ct);
        progress?.Report("uv downloaded successfully.");
        return LocalUvExe;
    }

    /// <summary>
    /// Creates a new virtual environment at <paramref name="venvPath"/> using
    /// <c>uv venv --python {pythonVersion}</c>. First use may download a full CPython
    /// toolchain, so this runs with <see cref="LongOperationTimeoutMs"/>.
    /// </summary>
    /// <param name="uvPath">Path to the uv binary.</param>
    /// <param name="venvPath">Directory where the venv will be created.</param>
    /// <param name="pythonVersion">Python version string, e.g. "3.11".</param>
    /// <param name="progress">Receives status updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">If venv creation fails.</exception>
    public async Task CreateVenvAsync(
        string uvPath,
        string venvPath,
        string pythonVersion,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(venvPath)!);
        progress?.Report($"Creating Python {pythonVersion} venv at {venvPath}...");

        var (exitCode, _, stderr) = await RunProcessAsync(
            _launchFactory, uvPath, new[] { "venv", "--python", pythonVersion, venvPath },
            ct, LongOperationTimeoutMs);

        if (exitCode != 0)
            throw new InvalidOperationException(
                $"uv venv creation failed (exit {exitCode}): {stderr}");

        progress?.Report("Virtual environment created.");
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static string? FindUvOnPath()
    {
        var uvName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "uv.exe" : "uv";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, uvName);
            if (File.Exists(candidate))
                return candidate;
        }

        // Check uv's default self-install location
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var uvDefault = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "uv", "bin", "uv.exe")
            : Path.Combine(home, ".local", "bin", "uv");

        return File.Exists(uvDefault) ? uvDefault : null;
    }

    private static async Task DownloadUvAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var url = GetUvDownloadUrl();
        Directory.CreateDirectory(ToolsDir);

        var tempFile = Path.GetTempFileName();
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Lunima/1.0");

            progress?.Report($"Downloading uv from {url}...");
            await using var stream = await http.GetStreamAsync(url, ct);
            await using var file = File.Create(tempFile);
            await stream.CopyToAsync(file, ct);
        }
        catch (Exception ex)
        {
            File.Delete(tempFile);
            if (ex is OperationCanceledException)
                throw;
            throw new InvalidOperationException(
                $"Failed to download uv: {ex.Message}\nInstall uv manually from https://docs.astral.sh/uv/", ex);
        }

        ExtractUvBinary(tempFile, progress);
        File.Delete(tempFile);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(LocalUvExe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void ExtractUvBinary(string archivePath, IProgress<string>? progress)
    {
        progress?.Report("Extracting uv binary...");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("uv.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("uv.exe not found in downloaded archive.");
            entry.ExtractToFile(LocalUvExe, overwrite: true);
        }
        else
        {
            ExtractFromTarGz(archivePath, "uv", LocalUvExe);
        }
    }

    private static void ExtractFromTarGz(string archivePath, string entryName, string destPath)
    {
        using var fs = File.OpenRead(archivePath);
        using var gz = new System.IO.Compression.GZipStream(fs, CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gz);

        while (tar.GetNextEntry() is { } entry)
        {
            if (!Path.GetFileName(entry.Name).Equals(entryName, StringComparison.Ordinal))
                continue;
            entry.ExtractToFile(destPath, overwrite: true);
            return;
        }

        throw new InvalidOperationException(
            $"'{entryName}' not found in downloaded archive.");
    }

    private static string GetUvDownloadUrl()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";

        var isArm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return isArm
                ? "https://github.com/astral-sh/uv/releases/latest/download/uv-aarch64-apple-darwin.tar.gz"
                : "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-apple-darwin.tar.gz";

        return isArm
            ? "https://github.com/astral-sh/uv/releases/latest/download/uv-aarch64-unknown-linux-musl.tar.gz"
            : "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-unknown-linux-musl.tar.gz";
    }

    /// <summary>
    /// Runs a process built via <paramref name="launchFactory"/> and captures its output.
    /// A user cancellation throws <see cref="OperationCanceledException"/>; a pure timeout
    /// returns exit code -1 with a "timed out" error so callers report it as a failure.
    /// </summary>
    internal static async Task<(int exitCode, string output, string error)> RunProcessAsync(
        ProcessLaunchFactory launchFactory,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        int timeoutMs = 120_000)
    {
        if (!launchFactory.TryBuild(fileName, arguments, null, null, out var startInfo, out var buildError))
            throw new InvalidOperationException($"Cannot launch '{fileName}': {buildError}");

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo };
        ct.ThrowIfCancellationRequested();
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            ct.ThrowIfCancellationRequested();
            return (-1, string.Empty, "Operation timed out.");
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }
}
