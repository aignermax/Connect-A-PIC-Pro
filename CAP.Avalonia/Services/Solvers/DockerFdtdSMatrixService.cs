using System.Collections.Concurrent;
using System.Diagnostics;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

/// <summary>
/// Implements <see cref="IFdtdSMatrixService"/> by running the open-source Meep
/// FDTD solver inside a pinned Docker image (Meep is conda-only and has no native
/// Windows build). The image is built on first use if absent. The component GDS
/// directory is volume-mounted; the request is passed as a spec file (robust under
/// MPI, unlike stdin). Raw stderr is surfaced on failure — no silent fallback.
/// </summary>
public class DockerFdtdSMatrixService : IFdtdSMatrixService
{
    /// <summary>Default per-solve timeout. 3D runs can take many minutes.</summary>
    public static readonly TimeSpan DefaultSolveTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Timeout for the one-time image build (conda solve + downloads).</summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(40);

    private const string ContainerDataDir = "/data";
    private const string ContainerScript = "/work/fdtd_sparams.py";

    /// <summary>Label stamped on every solver container so orphans can be reaped.</summary>
    private const string ContainerLabel = "lunima.fdtd=1";

    /// <summary>
    /// Target platform for the Docker image. conda-forge pymeep (MPI/MPICH variant)
    /// does not publish a linux-aarch64 wheel, so we pin to linux/amd64 for all hosts
    /// (Apple Silicon runs it under emulation).
    /// </summary>
    private const string FdtdPlatform = "linux/amd64";

    /// <summary>Names of containers currently running, killed on process exit.</summary>
    private static readonly ConcurrentDictionary<string, byte> ActiveContainers = new();
    private static int _exitHookInstalled;
    private static int _orphansReaped;

    /// <summary>
    /// Serialises solves process-wide. FDTD already saturates every core via mpirun,
    /// so overlapping runs only oversubscribe the CPU (and double the /dev/shm + RAM
    /// pressure) — they don't finish sooner. One at a time, each gets the full machine.
    /// </summary>
    internal static readonly SemaphoreSlim SolveGate = new(1, 1);

    /// <summary>Shared-memory budget per MPI rank in MB (UCX inter-rank buffers).</summary>
    private const int ShmMbPerRank = 256;

    /// <summary>Floor for /dev/shm so even a 1-2 core machine has headroom.</summary>
    private const int ShmFloorMb = 2048;

    /// <summary>Ceiling so we never request an absurd tmpfs cap on big machines.</summary>
    private const int ShmCeilingMb = 16384;

    private readonly ProcessLaunchFactory _launchFactory;
    private readonly string _dockerExe;
    private readonly string _imageTag;
    private readonly string _dockerfilePath;
    private readonly string _buildContext;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes the service.</summary>
    /// <param name="imageTag">Pinned image tag, e.g. "lunima-meep:1".</param>
    /// <param name="dockerfilePath">Path to the Dockerfile used to build the image.</param>
    /// <param name="buildContext">Docker build context (repo root, so the script can be COPYed).</param>
    /// <param name="dockerExecutable">Docker CLI override (default "docker").</param>
    /// <param name="timeout">Optional per-solve timeout.</param>
    /// <param name="launchFactory">Factory used to resolve the Docker CLI and build ProcessStartInfo
    ///     with a platform-aware PATH. Optional: production DI injects the shared singleton; tests and
    ///     non-DI construction fall back to a working default, so there is no test churn.</param>
    public DockerFdtdSMatrixService(
        string imageTag, string dockerfilePath, string buildContext,
        string? dockerExecutable = null, TimeSpan? timeout = null,
        ProcessLaunchFactory? launchFactory = null)
    {
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
        _imageTag = imageTag ?? throw new ArgumentNullException(nameof(imageTag));
        _dockerfilePath = dockerfilePath ?? throw new ArgumentNullException(nameof(dockerfilePath));
        _buildContext = buildContext ?? throw new ArgumentNullException(nameof(buildContext));
        _dockerExe = _launchFactory.ResolveExecutable(dockerExecutable ?? "docker") ?? "docker";
        _timeout = timeout ?? DefaultSolveTimeout;
    }

    /// <inheritdoc/>
    public async Task<FdtdSMatrixResult> SolveAsync(
        FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var hasGds = !string.IsNullOrWhiteSpace(request.GdsPath) && File.Exists(request.GdsPath);
        if (!hasGds && request.Polygons.Count == 0)
            return FdtdSMatrixResult.Fail("No geometry supplied: provide either a GDS file or polygons.");

        // Only one solve runs at a time (see SolveGate): a second is queued rather
        // than left to fight the first for the CPU. WaitAsync throws if the caller
        // cancels while queued — handled upstream as a normal cancel, not an error.
        if (SolveGate.CurrentCount == 0)
            progress?.Report("Waiting for another FDTD run to finish…");
        await SolveGate.WaitAsync(ct);
        try
        {
            return await SolveOnceAsync(request, hasGds, progress, ct);
        }
        finally
        {
            SolveGate.Release();
        }
    }

    private async Task<FdtdSMatrixResult> SolveOnceAsync(
        FdtdSMatrixRequest request, bool hasGds, IProgress<string>? progress, CancellationToken ct)
    {
        InstallExitHook();
        // Reap leftover containers from a previous (hard-killed) session ONCE per
        // process. Doing it before every solve would `docker rm -f` the containers
        // of concurrently-running solves in THIS session (they share the label) and
        // kill them mid-run ("No JSON found in FDTD solver output").
        if (Interlocked.Exchange(ref _orphansReaped, 1) == 0)
            await ReapOrphanContainersAsync();

        var provision = await EnsureImageAsync(ct);
        if (provision != null)
            return provision;

        // Working dir is mounted at /data. With a GDS we mount its folder; with
        // polygons there is no file, so we use a throwaway temp folder for the spec.
        var (workingDir, isTempDir) = hasGds
            ? (Path.GetDirectoryName(Path.GetFullPath(request.GdsPath))!, false)
            : (CreateTempDir(), true);
        var specPath = Path.Combine(workingDir, "_fdtd_request.json");
        var containerGds = hasGds ? $"{ContainerDataDir}/{Path.GetFileName(request.GdsPath)}" : string.Empty;
        var containerName = "lunima-fdtd-" + Guid.NewGuid().ToString("N");

        try
        {
            await File.WriteAllTextAsync(specPath, FdtdJsonContract.SerialiseRequest(request, containerGds), ct);
            ActiveContainers[containerName] = 0;

            var cores = ResolveCores(request);
            var runArgs = new[]
            {
                "run", "--rm",
                // Named + labelled so we can stop it on cancel/exit and reap orphans
                // (a bare `docker run` leaves the container running in the daemon when
                // the client process — or the whole app — is killed).
                "--name", containerName,
                "--label", ContainerLabel,
                // MPICH/UCX uses shared memory (/dev/shm) for inter-rank transport.
                // Docker's default /dev/shm is only 64 MB, which makes MPI_Init fail
                // ("Not enough memory ... /dev/shm") once several ranks start. Scale it
                // with the rank count (which itself scales with the machine's cores).
                // /dev/shm is a tmpfs cap, not a reservation, so a generous size is free.
                $"--shm-size={ResolveShmMb(cores)}m",
                // conda-forge pymeep mpi_mpich lacks a linux-aarch64 build today.
                "--platform", FdtdPlatform,
                "-v", $"{ToDockerPath(workingDir)}:{ContainerDataDir}",
                _imageTag,
                "mpirun", "-np", cores.ToString(),
                "python", ContainerScript,
                $"--spec={ContainerDataDir}/_fdtd_request.json",
            };

            if (!_launchFactory.TryBuild(_dockerExe, runArgs, workingDir, null, out var si, out var launchError))
                return FdtdSMatrixResult.Fail($"Could not start Docker: {launchError}", missingDependency: "docker");

            var run = await SubprocessJsonRunner.RunAsync(si, string.Empty, _timeout, ct,
                onStderrLine: line => { if (IsProgressLine(line)) progress?.Report(CleanProgressLine(line)); });
            return MapRun(run);
        }
        finally
        {
            // Ensure the container is gone even on cancel/timeout (killing the docker
            // client does not stop the daemon-managed container; --rm only fires on
            // a clean exit). rm -f is a no-op if it already exited.
            await ForceRemoveContainerAsync(containerName);
            ActiveContainers.TryRemove(containerName, out _);
            try { if (File.Exists(specPath)) File.Delete(specPath); } catch { /* best-effort */ }
            if (isTempDir)
                try { Directory.Delete(workingDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task ForceRemoveContainerAsync(string name)
    {
        var rmArgs = new[] { "rm", "-f", name };
        if (!_launchFactory.TryBuild(_dockerExe, rmArgs, null, null, out var si, out _))
            return;
        try { await SubprocessJsonRunner.RunAsync(si, string.Empty, TimeSpan.FromSeconds(20), CancellationToken.None); }
        catch { /* best-effort */ }
    }

    /// <summary>Removes any solver containers left over from a previous (crashed) session.</summary>
    private async Task ReapOrphanContainersAsync()
    {
        var psArgs = new[] { "ps", "-aq", "--filter", $"label={ContainerLabel}" };
        if (!_launchFactory.TryBuild(_dockerExe, psArgs, null, null, out var list, out _))
            return;
        SubprocessJsonRunner.RunResult res;
        try { res = await SubprocessJsonRunner.RunAsync(list, string.Empty, TimeSpan.FromSeconds(20), CancellationToken.None); }
        catch { return; }

        if (res.Outcome != SubprocessJsonRunner.Outcome.Completed) return;
        foreach (var id in res.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await ForceRemoveContainerAsync(id);
    }

    private void InstallExitHook()
    {
        if (Interlocked.Exchange(ref _exitHookInstalled, 1) == 1) return;
        var dockerExe = _dockerExe;
        var factory = _launchFactory;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var name in ActiveContainers.Keys)
            {
                try
                {
                    var exitArgs = new[] { "rm", "-f", name };
                    if (!factory.TryBuild(dockerExe, exitArgs, null, null, out var si, out _))
                        continue;
                    Process.Start(si)?.WaitForExit(5000);
                }
                catch { /* best-effort on shutdown */ }
            }
        };
    }

    private static string CreateTempDir()
    {
        string baseDir;
        if (OperatingSystem.IsMacOS())
        {
            // Docker Desktop on macOS only auto-shares a few host paths by default,
            // ~/Library/Caches among them; placing the bind-mount source here avoids
            // requiring the user to add /var/folders to Docker's file-sharing list.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            baseDir = Path.Combine(home, "Library", "Caches", "lunima-fdtd");
        }
        else
        {
            baseDir = Path.GetTempPath();
        }

        var dir = Path.Combine(baseDir, "lunima-fdtd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static FdtdSMatrixResult MapRun(SubprocessJsonRunner.RunResult run) => run.Outcome switch
    {
        SubprocessJsonRunner.Outcome.StartFailed =>
            FdtdSMatrixResult.Fail($"Could not start Docker: {run.StartError}", missingDependency: "docker"),
        SubprocessJsonRunner.Outcome.Cancelled => FdtdSMatrixResult.Fail("FDTD solve was cancelled."),
        SubprocessJsonRunner.Outcome.TimedOut => FdtdSMatrixResult.Fail("FDTD solver timed out."),
        _ => FdtdJsonContract.ParseOutput(run.Stdout, run.Stderr),
    };

    /// <summary>
    /// Ensures the solver image exists, building it from the Dockerfile if not.
    /// Returns null on success, or a failure result describing what went wrong.
    /// </summary>
    public async Task<FdtdSMatrixResult?> EnsureImageAsync(CancellationToken ct = default)
    {
        var inspectArgs = new[] { "image", "inspect", _imageTag };
        if (!_launchFactory.TryBuild(_dockerExe, inspectArgs, null, null, out var inspect, out var inspectErr))
            return FdtdSMatrixResult.Fail($"Could not start Docker: {inspectErr}", missingDependency: "docker");
        var probe = await SubprocessJsonRunner.RunAsync(inspect, string.Empty, TimeSpan.FromSeconds(30), ct);

        if (probe.Outcome == SubprocessJsonRunner.Outcome.StartFailed)
            return FdtdSMatrixResult.Fail($"Could not start Docker: {probe.StartError}", missingDependency: "docker");
        if (probe.Outcome == SubprocessJsonRunner.Outcome.Completed && probe.ExitCode == 0)
            return null; // image present

        var buildArgs = new[]
        {
            "build",
            // conda-forge pymeep mpi_mpich lacks a linux-aarch64 build today.
            "--platform", FdtdPlatform,
            "-f", _dockerfilePath,
            "-t", _imageTag,
            _buildContext,
        };
        if (!_launchFactory.TryBuild(_dockerExe, buildArgs, null, null, out var build, out var buildErr))
            return FdtdSMatrixResult.Fail($"Could not start Docker: {buildErr}", missingDependency: "docker");
        var built = await SubprocessJsonRunner.RunAsync(build, string.Empty, BuildTimeout, ct);

        if (built.Outcome == SubprocessJsonRunner.Outcome.Completed && built.ExitCode == 0)
            return null;
        return FdtdSMatrixResult.Fail(
            $"Failed to provision FDTD image '{_imageTag}'. {built.Outcome}.", rawStderr: built.Stderr);
    }

    /// <summary>
    /// Picks an MPI rank count. 3D is capped lower because each rank holds a slice
    /// of the 3D grid plus DFT monitors — too many ranks exhausted RAM and the OS
    /// killed the run (the original 24-rank OOM). Honours an explicit request value.
    /// </summary>
    private static int ResolveCores(FdtdSMatrixRequest request)
    {
        if (request.Cores > 0)
            return request.Cores;
        var available = Math.Max(1, Environment.ProcessorCount);
        return request.Is3D ? Math.Min(available, 8) : Math.Min(available, 16);
    }

    /// <summary>
    /// Sizes the container's /dev/shm to the rank count (UCX shared-memory transport
    /// grows with ranks), clamped to a sane floor/ceiling. Since /dev/shm is a tmpfs
    /// cap rather than an upfront reservation, over-provisioning is harmless.
    /// </summary>
    internal static int ResolveShmMb(int cores) =>
        Math.Clamp(cores * ShmMbPerRank, ShmFloorMb, ShmCeilingMb);

    /// <summary>
    /// Keeps the noise down: only forward solver lines that carry useful progress
    /// (a tqdm/meep percentage or time-step), not every warning.
    /// </summary>
    private static bool IsProgressLine(string line) =>
        line.Contains('%') || line.Contains("time step", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Meep progress", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strips the tqdm bar art (Unicode block-element glyphs and surrounding pipes)
    /// and the log/warning tail that Meep concatenates onto the same line (the bar is
    /// written with '\r', so a following warning lands on the same "line"). Returns
    /// just the useful "50% 3/4 [00:03&lt;00:01, 1.2it/s]" part.
    /// </summary>
    internal static string CleanProgressLine(string line)
    {
        var noBlocks = System.Text.RegularExpressions.Regex.Replace(line, @"[▀-▟|]+", " ");
        // The tqdm progress ends at the first ']'; anything after it (e.g. a
        // "/opt/conda/.../meep/__init__.py:…: ComplexWarning") is concatenated noise.
        var bracket = noBlocks.IndexOf(']');
        if (bracket >= 0)
            noBlocks = noBlocks[..(bracket + 1)];
        return System.Text.RegularExpressions.Regex.Replace(noBlocks, @"\s{2,}", " ").Trim();
    }

    private const string DockerInstallUrl = "https://www.docker.com/products/docker-desktop/";

    /// <inheritdoc/>
    public async Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        // `docker version --format {{.Server.Version}}` prints the engine version
        // when the daemon is reachable; it fails (non-zero) if Docker is installed
        // but the engine isn't running, and won't start at all if Docker is absent.
        var versionArgs = new[] { "version", "--format", "{{.Server.Version}}" };
        if (!_launchFactory.TryBuild(_dockerExe, versionArgs, null, null, out var si, out _))
            return FdtdAvailability.Unavailable(
                $"Docker is not installed (or not on PATH). FDTD needs Docker Desktop — install it from {DockerInstallUrl}, then retry.");

        var run = await SubprocessJsonRunner.RunAsync(si, string.Empty, TimeSpan.FromSeconds(20), ct);

        if (run.Outcome == SubprocessJsonRunner.Outcome.StartFailed)
            return FdtdAvailability.Unavailable(
                $"Docker is not installed (or not on PATH). FDTD needs Docker Desktop — install it from {DockerInstallUrl}, then retry.");

        if (run.Outcome == SubprocessJsonRunner.Outcome.Completed && run.ExitCode == 0
            && !string.IsNullOrWhiteSpace(run.Stdout))
            return FdtdAvailability.Available($"Docker engine {run.Stdout.Trim()} ready.");

        return FdtdAvailability.Unavailable(
            "Docker is installed but the engine isn't running. Start Docker Desktop and try again.");
    }

    private static string ToDockerPath(string path) => path.Replace('\\', '/');
}
