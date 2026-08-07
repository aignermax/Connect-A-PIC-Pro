using System.Diagnostics;
using CAP_Core.Export;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;
using UnitTests;

namespace UnitTests.Export;

/// <summary>
/// Integration tests for the complete export flow with dynamic GDS filename matching (Issue #172).
/// Tests the full vertical slice: Core (SimpleNazcaExporter) → Service (GdsExportService) → ViewModel (GdsExportViewModel).
/// </summary>
public class DynamicGdsFilenameIntegrationTests
{
    [Fact]
    public async Task CompleteExportFlow_GeneratesMatchingPythonAndGdsFiles()
    {
        var python = FindPython();
        if (python == null || !IsNazcaInstalled(python)) return; // Skip if environment not ready

        // Arrange
        var exporter = new SimpleNazcaExporter();
        var exportService = new GdsExportService();
        var viewModel = new GdsExportViewModel(exportService);

        var canvas = CreateMinimalCanvas();

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cap_integration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var scriptPath = Path.Combine(tmpDir, "test_design.py");
        var expectedGdsPath = Path.Combine(tmpDir, "test_design.gds");

        try
        {
            // Act - Simulate complete export flow
            // 1. Export Python script
            var nazcaCode = exporter.Export(canvas);
            await File.WriteAllTextAsync(scriptPath, nazcaCode);

            // 2. Execute script to generate GDS
            viewModel.GenerateGdsEnabled = true;
            var result = await viewModel.ExportScriptToGdsAsync(scriptPath);

            // Assert
            result.Success.ShouldBeTrue($"Export should succeed. Error: {result.ErrorMessage}");
            result.ScriptPath.ShouldBe(scriptPath);
            result.GdsPath.ShouldBe(expectedGdsPath);

            File.Exists(scriptPath).ShouldBeTrue();
            File.Exists(expectedGdsPath).ShouldBeTrue();

            new FileInfo(expectedGdsPath).Length.ShouldBeGreaterThan(0);

            viewModel.LastExportStatus.ShouldContain("successfully");
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteExportFlow_WithDifferentFilenames_GeneratesCorrectGdsNames()
    {
        var python = FindPython();
        if (python == null || !IsNazcaInstalled(python)) return;

        // Arrange
        var exporter = new SimpleNazcaExporter();
        var exportService = new GdsExportService();
        var canvas = CreateMinimalCanvas();

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cap_multifile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var testCases = new[]
        {
            ("chip_v1.py", "chip_v1.gds"),
            ("final_design.py", "final_design.gds"),
            ("test.py", "test.gds")
        };

        try
        {
            foreach (var (scriptName, expectedGdsName) in testCases)
            {
                var scriptPath = Path.Combine(tmpDir, scriptName);
                var expectedGdsPath = Path.Combine(tmpDir, expectedGdsName);

                // Act
                var nazcaCode = exporter.Export(canvas);
                await File.WriteAllTextAsync(scriptPath, nazcaCode);

                var result = await exportService.ExportToGdsAsync(scriptPath, generateGds: true);

                // Assert
                result.Success.ShouldBeTrue($"Export of {scriptName} should succeed");
                result.GdsPath.ShouldBe(expectedGdsPath, $"GDS path should match script name for {scriptName}");
                File.Exists(expectedGdsPath).ShouldBeTrue($"{expectedGdsName} should exist");
            }
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportFlow_WithGenerateGdsDisabled_OnlyCreatesScript()
    {
        // Arrange
        var exporter = new SimpleNazcaExporter();
        var exportService = new GdsExportService();
        var viewModel = new GdsExportViewModel(exportService);

        var canvas = CreateMinimalCanvas();

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cap_noexport_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var scriptPath = Path.Combine(tmpDir, "test_design.py");
        var gdsPath = Path.Combine(tmpDir, "test_design.gds");

        try
        {
            // Act
            var nazcaCode = exporter.Export(canvas);
            await File.WriteAllTextAsync(scriptPath, nazcaCode);

            viewModel.GenerateGdsEnabled = false;
            var result = await viewModel.ExportScriptToGdsAsync(scriptPath);

            // Assert
            result.Success.ShouldBeTrue();
            result.ScriptPath.ShouldBe(scriptPath);
            result.GdsPath.ShouldBeNull("GDS should not be generated when disabled");
            result.Status.ShouldContain("skipped");

            File.Exists(scriptPath).ShouldBeTrue();
            File.Exists(gdsPath).ShouldBeFalse("GDS file should not exist");
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportedScript_CanBeExecutedManually_ProducesGds()
    {
        var python = FindPython();
        if (python == null || !IsNazcaInstalled(python)) return;

        // Arrange
        var exporter = new SimpleNazcaExporter();
        var canvas = CreateMinimalCanvas();

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cap_manual_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var scriptPath = Path.Combine(tmpDir, "manual_design.py");
        var expectedGdsPath = Path.Combine(tmpDir, "manual_design.gds");

        try
        {
            // Act - Export script only, then manually execute
            var nazcaCode = exporter.Export(canvas);
            await File.WriteAllTextAsync(scriptPath, nazcaCode);

            // Manually execute the script (simulating user running "python manual_design.py")
            var (exitCode, stdout, stderr) = await RunPythonAsync(python, $"\"{scriptPath}\"");

            // Assert
            exitCode.ShouldBe(0, $"Manual execution failed.\nStderr: {stderr}");
            stdout.ShouldContain("manual_design.gds");
            File.Exists(expectedGdsPath).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void ExportedScript_FooterStructure_IsCorrect()
    {
        // Arrange
        var exporter = new SimpleNazcaExporter();
        var canvas = CreateMinimalCanvas();

        // Act
        var script = exporter.Export(canvas);

        // Assert - Verify footer contains all required components in correct order
        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Find the footer section
        var returnIndex = Array.FindIndex(lines, l => l.Contains("return design"));
        returnIndex.ShouldBeGreaterThan(0);

        // The design cell is exported directly as the GDS top cell (topcells=[design]) —
        // the old design.put() nested it under nazca's default 'nazca' wrapper cell,
        // which then became the file's only top-cell candidate on re-import.
        var createIndex = Array.FindIndex(lines, l => l.Contains("design = create_design()"));
        var osImportIndex = Array.FindIndex(lines, l => l.Trim() == "import os");
        var sysImportIndex = Array.FindIndex(lines, l => l.Trim() == "import sys");
        var scriptPathIndex = Array.FindIndex(lines, l => l.Contains("script_path = os.path.abspath(__file__)"));
        var gdsFilenameIndex = Array.FindIndex(lines, l => l.Contains("gds_filename = os.path.splitext(script_path)[0] + '.gds'"));
        var exportIndex = Array.FindIndex(lines, l => l.Contains("nd.export_gds(topcells=[design], filename=gds_filename)"));
        var printIndex = Array.FindIndex(lines, l => l.Contains("print(f'GDS exported to: {gds_filename}')"));

        // All elements should be present
        createIndex.ShouldBeGreaterThan(0);
        osImportIndex.ShouldBeGreaterThan(0);
        sysImportIndex.ShouldBeGreaterThan(0);
        scriptPathIndex.ShouldBeGreaterThan(0);
        gdsFilenameIndex.ShouldBeGreaterThan(0);
        exportIndex.ShouldBeGreaterThan(0);
        printIndex.ShouldBeGreaterThan(0);
        script.ShouldNotContain("design.put()");

        // Order should be correct
        createIndex.ShouldBeLessThan(osImportIndex);
        osImportIndex.ShouldBeLessThan(sysImportIndex);
        sysImportIndex.ShouldBeLessThan(scriptPathIndex);
        scriptPathIndex.ShouldBeLessThan(gdsFilenameIndex);
        gdsFilenameIndex.ShouldBeLessThan(exportIndex);
        exportIndex.ShouldBeLessThan(printIndex);
    }

    /// <summary>
    /// Creates a minimal canvas with one component for testing.
    /// </summary>
    private static DesignCanvasViewModel CreateMinimalCanvas()
    {
        var canvas = new DesignCanvasViewModel();

        // Add a simple component using the test factory
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = "GC1";
        component.NazcaFunctionName = "demo.io";
        component.PhysicalX = 0;
        component.PhysicalY = 0;
        component.RotationDegrees = 0;

        canvas.AddComponent(component, "GratingCoupler");
        return canvas;
    }

    private static string? FindPython()
    {
        foreach (var cmd in new[] { "python", "python3" })
        {
            try
            {
                var psi = new ProcessStartInfo(cmd, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                if (proc?.ExitCode == 0) return cmd;
            }
            catch { }
        }
        return null;
    }

    private static bool IsNazcaInstalled(string python)
    {
        try
        {
            var psi = new ProcessStartInfo(python, "-c \"import nazca\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunPythonAsync(
        string python, string args, int timeoutMs = 30000)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        return (process.ExitCode, output, error);
    }
}
