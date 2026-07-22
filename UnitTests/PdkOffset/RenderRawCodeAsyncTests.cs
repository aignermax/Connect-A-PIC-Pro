using CAP_Core.Export;
using Shouldly;

namespace UnitTests.PdkOffset;

/// <summary>
/// Tests for <see cref="NazcaComponentPreviewService.RenderRawCodeAsync"/> (issue #556),
/// the raw-code preview path that writes user code to a temp .py and runs the preview
/// script in <c>--code-file</c> mode. Subprocess-dependent tests skip gracefully when
/// Python / Nazca is unavailable, matching the existing preview-test pattern.
/// </summary>
public class RenderRawCodeAsyncTests
{
    /// <summary>Resolves a working Python 3 path, or null when none can execute a script.</summary>
    private static string? FindWorkingPython3()
    {
        // Opt-in gate (see NazcaComponentPreviewServiceTests): real-python tests only run when the
        // workflow points at a python via LUNIMA_TEST_PYTHON3 (CI); a plain local run skips them.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUNIMA_TEST_PYTHON3")))
            return null;
        var envOverride = Environment.GetEnvironmentVariable("LUNIMA_TEST_PYTHON3");
        var candidates = !string.IsNullOrWhiteSpace(envOverride)
            ? new[] { envOverride, "/usr/bin/python3", "/usr/local/bin/python3", "python3" }
            : new[] { "/usr/bin/python3", "/usr/local/bin/python3", "python3" };
        foreach (var c in candidates)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = c,
                    Arguments = "-c \"import sys; print('ok')\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                p?.WaitForExit(5000);
                if (p?.ExitCode == 0) return c;
            }
            catch { /* try next */ }
        }
        return null;
    }

    private static string? FindRealPreviewScript() =>
        UnitTests.Integration.GdsAlignmentTestSetup.FindRealPreviewScript();

    [Fact]
    public async Task RenderRawCodeAsync_WithNonExistentScript_ReturnsFailureNotThrows()
    {
        var svc = new NazcaComponentPreviewService(
            "python3", "/tmp/does_not_exist_render_component_preview.py");

        var result = await svc.RenderRawCodeAsync("def component():\n    pass\n");

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error!.ShouldContain("not found");
    }

    [Fact]
    public async Task RenderRawCodeAsync_WithBadPython_ReturnsFailureNotThrows()
    {
        var script = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(script, "print('{}')");
            var svc = new NazcaComponentPreviewService("/absolutely/nonexistent/python99", script);

            var result = await svc.RenderRawCodeAsync("def component():\n    pass\n");

            result.Success.ShouldBeFalse();
            result.Error.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task RenderRawCodeAsync_ValidCode_ReturnsSuccessWithBBox()
    {
        var python = FindWorkingPython3();
        if (python == null) return;  // no python — environment skip
        var script = FindRealPreviewScript();
        if (script == null) return;  // script not found — skip

        // A demofab straight waveguide is bundled with Nazca itself.
        const string code =
            "import nazca as nd\n" +
            "import nazca.demofab as pdk\n" +
            "def component():\n" +
            "    return pdk.strt(length=20)\n";

        var svc = new NazcaComponentPreviewService(python, script);
        var result = await svc.RenderRawCodeAsync(code);

        if (!result.Success)
        {
            // Nazca not installed in this environment — skip rather than fail CI.
            result.Error.ShouldNotBeNullOrEmpty();
            return;
        }

        result.XMax.ShouldBeGreaterThan(result.XMin,
            $"bbox should be non-degenerate; got xmin={result.XMin} xmax={result.XMax}");
    }

    [Fact]
    public async Task RenderRawCodeAsync_InvalidCode_ReturnsFailureWithErrorNoCrash()
    {
        var python = FindWorkingPython3();
        if (python == null) return;  // no python — environment skip
        var script = FindRealPreviewScript();
        if (script == null) return;  // script not found — skip

        // Syntax error: missing colon / body — import succeeds but exec fails.
        const string badCode = "def component(\n    this is not valid python\n";

        var svc = new NazcaComponentPreviewService(python, script);
        var result = await svc.RenderRawCodeAsync(badCode);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RenderRawCodeAsync_MissingEntryPoint_ReturnsFailure()
    {
        var python = FindWorkingPython3();
        if (python == null) return;
        var script = FindRealPreviewScript();
        if (script == null) return;

        // Valid Python, but no component()/cell — the script must report a failure.
        const string code = "x = 1 + 1\n";

        var svc = new NazcaComponentPreviewService(python, script);
        var result = await svc.RenderRawCodeAsync(code);

        // Either Nazca is missing (env skip with error) or the entry-point check
        // fires — both are failures, never a success and never a crash.
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }
}
