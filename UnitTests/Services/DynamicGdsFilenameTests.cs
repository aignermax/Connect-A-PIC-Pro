using System.Diagnostics;
using System.Text;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;
using UnitTests;

namespace UnitTests.Services;

/// <summary>
/// Tests for dynamic GDS filename generation feature (Issue #172).
/// Verifies that exported Python scripts generate GDS files with matching names.
/// </summary>
public class DynamicGdsFilenameTests
{
    [Fact]
    public void ExportedScript_ContainsDynamicGdsFilenameCode()
    {
        // Arrange
        var exporter = new SimpleNazcaExporter();
        var canvas = CreateMinimalCanvas();

        // Act
        var script = exporter.Export(canvas);

        // Assert
        script.ShouldContain("import os");
        script.ShouldContain("import sys");
        script.ShouldContain("script_path = os.path.abspath(__file__)");
        script.ShouldContain("gds_filename = os.path.splitext(script_path)[0] + '.gds'");
        script.ShouldContain("nd.export_gds(topcells=[design], filename=gds_filename)");
        script.ShouldContain("print(f'GDS exported to: {gds_filename}')");
    }

    [Fact]
    public void ExportedScript_DoesNotContainHardcodedGdsFilename()
    {
        // Arrange
        var exporter = new SimpleNazcaExporter();
        var canvas = CreateMinimalCanvas();

        // Act
        var script = exporter.Export(canvas);

        // Assert
        // Should not have old hardcoded patterns
        script.ShouldNotContain("nd.export_gds()"); // Old footer without filename
        script.ShouldNotContain("filename='output.gds'"); // Old hardcoded filename
        script.ShouldNotContain("filename=\"output.gds\"");
    }

    [Fact]
    public void ExportedScript_GeneratesGdsWithMatchingFilename_WhenExecuted()
    {
        var python = FindPython();
        if (python == null) return; // Skip if no Python

        // Arrange
        var exporter = new SimpleNazcaExporter();
        var canvas = CreateMinimalCanvas();
        var script = exporter.Export(canvas);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cap_dynfilename_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var scriptPath = Path.Combine(tmpDir, "my_design.py");
        var expectedGdsPath = Path.Combine(tmpDir, "my_design.gds");

        try
        {
            File.WriteAllText(scriptPath, script);

            // Act
            var (exitCode, stdout, stderr) = RunPython(python, $"\"{scriptPath}\"", 60000);

            // Assert
            if (IsNazcaInstalled(python))
            {
                // If Nazca is available, GDS should be generated
                exitCode.ShouldBe(0, $"Script execution failed.\nStderr: {stderr}\nStdout: {stdout}");
                stdout.ShouldContain("my_design.gds");
                File.Exists(expectedGdsPath).ShouldBeTrue();

                // GDS should be non-empty
                new FileInfo(expectedGdsPath).Length.ShouldBeGreaterThan(0);
            }
            else
            {
                // If Nazca not installed, script should still be syntactically valid
                // (will fail at runtime, but that's expected)
                script.ShouldContain("nd.export_gds(topcells=[design], filename=gds_filename)");
            }
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void ExportedScript_GeneratesGdsWithDifferentNames_ForDifferentScriptNames()
    {
        var python = FindPython();
        if (python == null || !IsNazcaInstalled(python)) return; // Skip if no Python+Nazca

        // Arrange
        var exporter = new SimpleNazcaExporter();
        var canvas = CreateMinimalCanvas();
        var script = exporter.Export(canvas);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cap_multiname_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Export same design with different script names
            var script1Path = Path.Combine(tmpDir, "design_v1.py");
            var script2Path = Path.Combine(tmpDir, "design_v2.py");
            var gds1Path = Path.Combine(tmpDir, "design_v1.gds");
            var gds2Path = Path.Combine(tmpDir, "design_v2.gds");

            File.WriteAllText(script1Path, script);
            File.WriteAllText(script2Path, script);

            // Act
            var (exitCode1, stdout1, stderr1) = RunPython(python, $"\"{script1Path}\"", 60000);
            var (exitCode2, stdout2, stderr2) = RunPython(python, $"\"{script2Path}\"", 60000);

            // Assert
            exitCode1.ShouldBe(0, $"Script 1 failed: {stderr1}");
            exitCode2.ShouldBe(0, $"Script 2 failed: {stderr2}");

            File.Exists(gds1Path).ShouldBeTrue("design_v1.gds should exist");
            File.Exists(gds2Path).ShouldBeTrue("design_v2.gds should exist");

            stdout1.ShouldContain("design_v1.gds");
            stdout2.ShouldContain("design_v2.gds");
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void ExportedScript_HandlesPathsWithSpaces_Correctly()
    {
        var python = FindPython();
        if (python == null || !IsNazcaInstalled(python)) return;

        // Arrange
        var exporter = new SimpleNazcaExporter();
        var canvas = CreateMinimalCanvas();
        var script = exporter.Export(canvas);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cap test dir {Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var scriptPath = Path.Combine(tmpDir, "my design.py");
        var expectedGdsPath = Path.Combine(tmpDir, "my design.gds");

        try
        {
            File.WriteAllText(scriptPath, script);

            // Act
            var (exitCode, stdout, stderr) = RunPython(python, $"\"{scriptPath}\"", 60000);

            // Assert
            exitCode.ShouldBe(0, $"Script with spaces in path failed: {stderr}");
            File.Exists(expectedGdsPath).ShouldBeTrue(
                "GDS file should exist even with spaces in path");
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
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

    private static (int exitCode, string stdout, string stderr) RunPython(
        string python, string args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(python, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        return (proc.ExitCode, stdout, stderr);
    }
}
