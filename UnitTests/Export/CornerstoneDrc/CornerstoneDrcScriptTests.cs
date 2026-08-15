using Shouldly;
using Xunit;

namespace UnitTests.Export.CornerstoneDrc;

/// <summary>
/// Covers <c>scripts/run_cornerstone_drc.py</c> itself — report parsing, summary format and
/// exit-code contract — WITHOUT needing KLayout: <c>--parse-only</c> summarizes a fixture
/// marker database, and the error paths (missing GDS / missing klayout binary) are pure
/// script logic. Only a Python interpreter is required; the test skips cleanly without one.
/// </summary>
[Trait("Category", "Slow")]
public class CornerstoneDrcScriptTests : IDisposable
{
    private const int ExitClean = 0;
    private const int ExitViolations = 1;
    private const int ExitToolError = 2;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cornerstone-drc-script-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [SkippableFact]
    public async Task ParseOnly_BrokenReport_ExitsOneAndListsEachRuleWithItsCount()
    {
        var python = await ExternalToolProbes.FindPythonAsync();
        Skip.If(python == null, "No Python interpreter on PATH.");

        var report = WriteReport(
            ("Minimum gap violation (GDS203 &lt; 250nm)", 2),
            ("Minimum feature size violation (GDS203&lt; 250nm)", 1));

        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, "--parse-only", report);

        exitCode.ShouldBe(ExitViolations, $"stdout:\n{output}\nstderr:\n{error}");
        output.ShouldContain("2 x Minimum gap violation (GDS203 < 250nm)");
        output.ShouldContain("1 x Minimum feature size violation (GDS203< 250nm)");
        output.ShouldContain("FAILED: 3 DRC violation(s) across 2 rule(s).");
    }

    [SkippableFact]
    public async Task ParseOnly_CleanReport_ExitsZero()
    {
        var python = await ExternalToolProbes.FindPythonAsync();
        Skip.If(python == null, "No Python interpreter on PATH.");

        var report = WriteReport();

        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, "--parse-only", report);

        exitCode.ShouldBe(ExitClean, $"stdout:\n{output}\nstderr:\n{error}");
        output.ShouldContain("PASSED: 0 DRC violations.");
    }

    [SkippableFact]
    public async Task MissingGds_ExitsTwoWithToolError()
    {
        var python = await ExternalToolProbes.FindPythonAsync();
        Skip.If(python == null, "No Python interpreter on PATH.");

        var missing = Path.Combine(_root, "no-such-design.gds");
        var (exitCode, _, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, missing);

        exitCode.ShouldBe(ExitToolError);
        error.ShouldContain("GDS not found");
    }

    [SkippableFact]
    public async Task MissingKlayoutBinary_ExitsTwoWithToolError()
    {
        var python = await ExternalToolProbes.FindPythonAsync();
        Skip.If(python == null, "No Python interpreter on PATH.");

        Directory.CreateDirectory(_root);
        var gds = Path.Combine(_root, "any.gds");
        await File.WriteAllBytesAsync(gds, new byte[] { 0, 1, 2 });
        var nonexistentKlayout = Path.Combine(_root, "no-such-klayout-binary");

        var (exitCode, _, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, gds, "--klayout", nonexistentKlayout);

        exitCode.ShouldBe(ExitToolError);
        // An explicit --klayout override that does not resolve takes the script's
        // "klayout not found at '<path>'" branch; "no KLayout executable found" is
        // only printed when auto-discovery on PATH comes up empty.
        error.ShouldContain("klayout not found at");
        error.ShouldContain(nonexistentKlayout);
    }

    /// <summary>Writes a minimal .lyrdb marker database with the given (rule, count) items.</summary>
    private string WriteReport(params (string Rule, int Count)[] violations)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "report.lyrdb");
        using var writer = new StreamWriter(path);
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        writer.WriteLine("<report-database>");
        writer.WriteLine(" <description>DRC report</description>");
        writer.WriteLine(" <categories/>");
        writer.WriteLine(" <cells/>");
        writer.WriteLine(" <items>");
        foreach (var (rule, count) in violations)
            for (var i = 0; i < count; i++)
            {
                writer.WriteLine("  <item>");
                writer.WriteLine($"   <category>'{rule}'</category>");
                writer.WriteLine("   <cell>TOP</cell>");
                writer.WriteLine("  </item>");
            }
        writer.WriteLine(" </items>");
        writer.WriteLine("</report-database>");
        return path;
    }
}
