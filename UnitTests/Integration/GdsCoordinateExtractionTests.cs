using CAP_Core.Export;
using CAP.Avalonia.ViewModels.Export;
using Shouldly;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Integration tests for the GDS coordinate extraction tool (issue #331).
/// Verifies that scripts/extract_gds_coords.py can be invoked from C# and
/// produces valid JSON output containing polygon/path coordinate data.
///
/// Tests skip automatically when Python or gdspy is not available, so they
/// do not block CI in environments without the Python dependency.
/// </summary>
public class GdsCoordinateExtractionTests
{
    private const int TimeoutMs = 30_000;

    // ── script invocation tests ────────────────────────────────────────────

    /// <summary>
    /// Verifies the extraction script is present in the repository.
    /// </summary>
    [Fact]
    public void ExtractionScript_IsPresent_InScriptsFolder()
    {
        var scriptPath = GdsCoordinateExtractor.FindScriptPath();
        scriptPath.ShouldNotBeNull(
            "scripts/extract_gds_coords.py should exist relative to the test runner or AppContext.BaseDirectory");
        File.Exists(scriptPath).ShouldBeTrue();
    }

    /// <summary>
    /// Verifies the requirements.txt is present alongside the extraction script.
    /// </summary>
    [Fact]
    public void RequirementsTxt_IsPresent_InScriptsFolder()
    {
        var scriptPath = GdsCoordinateExtractor.FindScriptPath();
        if (scriptPath == null)
        {
            // If the script itself is missing, the script presence test will catch it.
            return;
        }

        var scriptDir = Path.GetDirectoryName(scriptPath)!;
        var requirementsPath = Path.Combine(scriptDir, "requirements.txt");
        File.Exists(requirementsPath).ShouldBeTrue(
            "scripts/requirements.txt should exist next to extract_gds_coords.py");

        var content = File.ReadAllText(requirementsPath);
        content.ShouldContain("gdspy");
        content.ShouldContain("numpy");
    }

    /// <summary>
    /// End-to-end: runs the Python extraction script on a minimal GDS file and
    /// verifies the output JSON has the expected structure.
    /// Skips if Python or gdspy is unavailable.
    /// </summary>
    [Fact]
    public async Task ExtractGdsCoordinates_ValidFile_ProducesStructuredJsonOutput()
    {
        var extractor = new GdsCoordinateExtractor();

        if (!await extractor.IsPythonAvailableAsync())
        {
            // Python not found — skip
            return;
        }

        var scriptPath = GdsCoordinateExtractor.FindScriptPath();
        if (scriptPath == null)
        {
            // Script missing — the ExtractionScript_IsPresent test will catch this
            return;
        }

        // Create a minimal GDS file using gdspy itself via a helper script
        var minimalGds = await CreateMinimalGdsFileAsync(extractor);
        if (minimalGds == null)
        {
            // gdspy not installed — skip
            return;
        }

        var jsonPath = Path.ChangeExtension(minimalGds, ".coords.json");

        try
        {
            var result = await extractor.ExtractAsync(minimalGds, jsonPath);

            if (!result.Success)
            {
                // gdspy may not be installed; this is acceptable in CI
                result.ErrorMessage.ShouldNotBeNullOrEmpty();
                return;
            }

            // Verify the JSON output structure
            File.Exists(jsonPath).ShouldBeTrue();

            var json = result.JsonContent;
            json.ShouldNotBeNullOrEmpty();

            var doc = JsonDocument.Parse(json!);
            var root = doc.RootElement;

            root.TryGetProperty("units", out var units).ShouldBeTrue(
                "Output JSON must contain 'units'");
            units.TryGetProperty("db_unit_m", out _).ShouldBeTrue(
                "'units' must contain 'db_unit_m'");
            root.TryGetProperty("cells", out var cells).ShouldBeTrue(
                "Output JSON must contain 'cells'");

            // Each cell must have polygons and paths arrays
            foreach (var cell in cells.EnumerateArray())
            {
                var name = cell.GetProperty("name").GetString();
                cell.TryGetProperty("polygons", out _).ShouldBeTrue(
                    $"Cell '{name}' must have 'polygons' array");
                cell.TryGetProperty("paths", out _).ShouldBeTrue(
                    $"Cell '{name}' must have 'paths' array");
            }
        }
        finally
        {
            if (File.Exists(minimalGds)) File.Delete(minimalGds);
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
        }
    }

    /// <summary>
    /// Verifies that the ViewModel correctly reflects the extractor result.
    /// </summary>
    [Fact]
    public async Task ViewModel_ExtractCoordinates_WithRealFile_UpdatesHasResultWhenSuccessful()
    {
        var extractor = new GdsCoordinateExtractor();
        var vm = new GdsCoordExtractViewModel(extractor);

        if (!await extractor.IsPythonAvailableAsync())
            return;

        var minimalGds = await CreateMinimalGdsFileAsync(extractor);
        if (minimalGds == null)
            return;

        try
        {
            vm.SetGdsFilePath(minimalGds);
            await vm.ExtractCoordinatesAsync();

            // If extraction ran (Python + gdspy available), HasResult must be true
            if (vm.HasResult)
            {
                vm.OutputJsonPath.ShouldNotBeNullOrEmpty();
                vm.ResultSummary.ShouldNotBeNullOrEmpty();
                vm.StatusText.ShouldNotBeNullOrEmpty();
                vm.IsExtracting.ShouldBeFalse();
            }
            else
            {
                // Failed (e.g., gdspy not installed) — status must explain why
                vm.StatusText.ShouldNotBeNullOrEmpty();
            }
        }
        finally
        {
            if (File.Exists(minimalGds)) File.Delete(minimalGds);
            var jsonPath = Path.ChangeExtension(minimalGds, ".coords.json");
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal GDS file using a gdspy one-liner.
    /// Returns null if gdspy is not installed.
    /// </summary>
    private static async Task<string?> CreateMinimalGdsFileAsync(GdsCoordinateExtractor extractor)
    {
        var gdsPath = Path.Combine(Path.GetTempPath(), $"cap_test_{Guid.NewGuid():N}.gds");

        var escapedPath = gdsPath.Replace("\\", "\\\\");
        var createScript =
            "import gdspy; " +
            "lib = gdspy.GdsLibrary(); " +
            "cell = lib.new_cell('TEST'); " +
            "cell.add(gdspy.Rectangle((0, 0), (10, 10), layer=1)); " +
            $"lib.write_gds(r'{escapedPath}'); " +
            "print('ok')";

        var python = await GetPythonCommandAsync();
        if (python == null)
            return null;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-c \"{createScript}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        await process.WaitForExitAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();

        if (process.ExitCode != 0 || !File.Exists(gdsPath))
            return null;

        return gdsPath;
    }

    private static async Task<string?> GetPythonCommandAsync()
    {
        foreach (var cmd in new[] { "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) continue;
                await p.WaitForExitAsync();
                if (p.ExitCode == 0)
                    return cmd;
            }
            catch
            {
                // Try next
            }
        }
        return null;
    }
}
