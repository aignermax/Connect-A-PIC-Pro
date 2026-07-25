using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

// No test here touches Python or the network — the service must fail fast with
// actionable messages before anything is launched.
public class Tidy3dSMatrixServiceTests
{
    private static string ExistingScript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tidy3d_sparams_{Guid.NewGuid():N}.py");
        File.WriteAllText(path, "# test stub");
        return path;
    }

    [Fact]
    public async Task CheckAvailability_WithoutApiKey_ExplainsHowToFix()
    {
        var script = ExistingScript();
        try
        {
            var service = new Tidy3dSMatrixService("python3", script, () => null);

            var result = await service.CheckAvailabilityAsync();

            result.IsAvailable.ShouldBeFalse();
            result.Message.ShouldContain("API key");
            result.Message.ShouldContain("Settings");
            result.Reason.ShouldBe(FdtdUnavailableReason.MissingApiKey);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task CheckAvailability_WithMissingScript_ReportsScriptPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist", "tidy3d_sparams.py");
        var service = new Tidy3dSMatrixService("python3", missing, () => "key-123");

        var result = await service.CheckAvailabilityAsync();

        result.IsAvailable.ShouldBeFalse();
        result.Message.ShouldContain("tidy3d_sparams.py");
    }

    [Fact]
    public async Task Solve_WithoutGeometry_FailsWithoutLaunchingAnything()
    {
        var service = new Tidy3dSMatrixService("python3", "irrelevant.py", () => "key-123");

        var result = await service.SolveAsync(new FdtdSMatrixRequest());

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("geometry");
    }

    [Fact]
    public async Task Solve_WithoutApiKey_FailsWithMissingDependency()
    {
        var script = ExistingScript();
        try
        {
            var service = new Tidy3dSMatrixService("python3", script, () => "  ");

            var result = await service.SolveAsync(new FdtdSMatrixRequest
            {
                Polygons = new List<FdtdPolygon> { new() },
            });

            result.Success.ShouldBeFalse();
            result.MissingDependency.ShouldBe("tidy3d");
            result.Error.ShouldContain("API key");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task EstimateCost_WithoutApiKey_Fails()
    {
        var script = ExistingScript();
        try
        {
            var service = new Tidy3dSMatrixService("python3", script, () => string.Empty);

            var estimate = await service.EstimateCostAsync(new FdtdSMatrixRequest());

            estimate.Success.ShouldBeFalse();
            estimate.Error.ShouldContain("API key");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public void Service_ImplementsCostEstimatorCapability()
    {
        var service = new Tidy3dSMatrixService("python3", "s.py", () => null);

        service.ShouldBeAssignableTo<IFdtdSMatrixService>();
        service.ShouldBeAssignableTo<IFdtdCostEstimator>();
    }

    [Theory]
    [InlineData("LUNIMA_PROGRESS: task o1@0: running", "task o1@0: running")]
    [InlineData("LUNIMA_PROGRESS:2/3 simulations done", "2/3 simulations done")]
    public void TryGetProgressText_PrefixedLine_StripsPrefix(string line, string expected)
    {
        Tidy3dSMatrixService.TryGetProgressText(line, out var text).ShouldBeTrue();
        text.ShouldBe(expected);
    }

    [Theory]
    [InlineData("tidy3d warning: something")]
    [InlineData("LUNIMA_PROGRESS:")]
    [InlineData("LUNIMA_PROGRESS:   ")]
    [InlineData("LUNIMA_PROGRES: missing a letter")]
    public void TryGetProgressText_UnprefixedOrEmptyLine_IsNotProgress(string line)
    {
        Tidy3dSMatrixService.TryGetProgressText(line, out _).ShouldBeFalse();
    }

    // ── End-to-end with a real Python interpreter (skipped when none is present) ──

    private sealed class SyncProgress : IProgress<string>
    {
        public readonly List<string> Lines = new();
        public void Report(string value)
        {
            lock (Lines) Lines.Add(value);
        }
    }

    // Naive PATH probe mirroring PythonResolution.ResolvePythonExecutable, but
    // returning null when no interpreter answers so the e2e tests skip cleanly.
    private static string? ProbePython()
    {
        var factory = CAP_Core.Export.ProcessLaunchFactory.CreateDefault();
        foreach (var candidate in OperatingSystem.IsWindows()
                     ? new[] { "python", "python3" }
                     : new[] { "python3", "python" })
        {
            try
            {
                if (!factory.TryBuild(candidate, new[] { "--version" }, null, null, out var si, out _))
                    continue;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                using var p = System.Diagnostics.Process.Start(si);
                if (p == null) continue;
                p.WaitForExit(5000);
                if (p.ExitCode == 0) return candidate;
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    // A stub bridge that ignores the request, records its working directory and
    // emits a minimal valid solve-result JSON (plus stderr lines of both kinds).
    private static string WriteStubBridge(string dir, string cwdFile)
    {
        var script = Path.Combine(dir, "tidy3d_sparams.py");
        File.WriteAllText(script, $$"""
            import json, os, sys
            sys.stdin.read()
            with open(r"{{cwdFile}}", "w") as f:
                f.write(os.getcwd())
            print("LUNIMA_PROGRESS: task o1@0: running", file=sys.stderr, flush=True)
            print("tidy3d library noise", file=sys.stderr, flush=True)
            print(json.dumps({
                "success": True, "is_3d": True, "ports": [],
                "wavelengths": [], "s": {}, "energy_sum_per_input": {},
            }))
            """);
        return script;
    }

    [Fact]
    public async Task Solve_RunsBridgeInPerRunTempDir_AndCleansItUp()
    {
        var python = ProbePython();
        if (python == null) return;   // no interpreter in this environment — covered locally

        var dir = Path.Combine(Path.GetTempPath(), $"tidy3d-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var cwdFile = Path.Combine(dir, "cwd.txt");
            var script = WriteStubBridge(dir, cwdFile);
            var service = new Tidy3dSMatrixService(python, script, () => "key-123");

            var result = await service.SolveAsync(new FdtdSMatrixRequest
            {
                Polygons = new List<FdtdPolygon> { new() },
                Ports = new[] { new FdtdPort { Name = "o1" } },
            });

            result.Success.ShouldBeTrue();
            var workDir = (await File.ReadAllTextAsync(cwdFile)).Trim();
            Path.GetFileName(workDir).ShouldStartWith("lunima-tidy3d-");
            Directory.Exists(workDir).ShouldBeFalse("the per-run temp dir must be cleaned up");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Solve_ForwardsOnlyPrefixedProgressLines()
    {
        var python = ProbePython();
        if (python == null) return;   // no interpreter in this environment — covered locally

        var dir = Path.Combine(Path.GetTempPath(), $"tidy3d-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var script = WriteStubBridge(dir, Path.Combine(dir, "cwd.txt"));
            var service = new Tidy3dSMatrixService(python, script, () => "key-123");
            var progress = new SyncProgress();

            var result = await service.SolveAsync(new FdtdSMatrixRequest
            {
                Polygons = new List<FdtdPolygon> { new() },
                Ports = new[] { new FdtdPort { Name = "o1" } },
            }, progress);

            result.Success.ShouldBeTrue();
            progress.Lines.ShouldContain("task o1@0: running");
            progress.Lines.ShouldNotContain("tidy3d library noise");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
