using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport;

/// <summary>
/// End-to-end: runs the generated gdsfactory script with a real Python and asserts a
/// GDS file is produced. Skips silently when no gdsfactory-capable interpreter is
/// available (CI without the ground-truth env). Locally, create one via
/// <c>%LOCALAPPDATA%\Lunima\tools\uv.exe venv %TEMP%\gf-groundtruth --python 3.12</c>
/// + <c>uv pip install gdsfactory ubcpdk</c>.
/// </summary>
public class GdsFactoryScriptExecutionTests
{
    private static string? FindGdsFactoryPython()
    {
        var candidates = new[]
        {
            Path.Combine(Path.GetTempPath(), "gf-groundtruth", "Scripts", "python.exe"),
            Path.Combine(Path.GetTempPath(), "gf-groundtruth", "bin", "python"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static DesignCanvasViewModel CreateSingleComponentCanvas(out CAP_Core.Components.Core.Component component)
    {
        var canvas = new DesignCanvasViewModel();
        component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = "Y1";
        component.NazcaFunctionName = "ebeam_y_1550";
        component.PhysicalX = 10;
        component.PhysicalY = 20;
        component.PhysicalPins.Add(new CAP_Core.Components.Core.PhysicalPin
        {
            Name = "opt1",
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 180,
        });
        canvas.AddComponent(component, "YBranch");
        return canvas;
    }

    private static async Task<(int exitCode, string output, string stderr)> RunPythonAsync(
        string python, params string[] args) =>
        await CAP_Core.Export.PythonEnvironmentManager.UvBootstrapper.RunProcessAsync(
            ProcessLaunchFactory.CreateDefault(), python, args,
            CancellationToken.None, timeoutMs: 300_000);

    [Fact]
    public async Task StandaloneScript_RunsAndGdsGeometryLandsOnThePlacementContract()
    {
        var python = FindGdsFactoryPython();
        if (python == null) return;   // environment not available — covered locally

        var canvas = CreateSingleComponentCanvas(out var component);
        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs));

        var dir = Path.Combine(Path.GetTempPath(), $"gf-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "design.py");
        var gdsPath = Path.Combine(dir, "design.gds");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script);
            var (exitCode, _, stderr) = await RunPythonAsync(python, scriptPath);
            exitCode.ShouldBe(0, $"script must run cleanly.\nstderr:\n{stderr}");
            File.Exists(gdsPath).ShouldBeTrue();

            // Geometry check with the same engine that wrote the file: the stub rectangle's
            // world bbox must match the NazcaCoordinateMapper placement contract.
            var placement = NazcaCoordinateMapper.GetCellPlacement(component, rawOverrideAnchor: null);
            var expected = (
                X0: placement.X - component.NazcaOriginOffsetX,
                Y0: placement.Y + component.NazcaOriginOffsetY - component.HeightMicrometers,
                X1: placement.X - component.NazcaOriginOffsetX + component.WidthMicrometers,
                Y1: placement.Y + component.NazcaOriginOffsetY);

            // Select the design cell by name — gdsfactory also writes a top-level
            // $$$CONTEXT_INFO$$$ metadata cell whose bbox ignores placements.
            var (bboxExit, bboxOut, bboxErr) = await RunPythonAsync(python, "-c",
                $"import gdstk; lib = gdstk.read_gds(r'{gdsPath}'); "
                + "top = [c for c in lib.cells if c.name == 'ConnectAPIC_Design'][0]; "
                + "b = top.bounding_box(); "
                + "print(f'{b[0][0]:.2f};{b[0][1]:.2f};{b[1][0]:.2f};{b[1][1]:.2f}')");
            bboxExit.ShouldBe(0, $"bbox probe must run.\nstderr:\n{bboxErr}");

            var parts = bboxOut.Trim().Split(';');
            double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected.X0, 0.01);
            double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected.Y0, 0.01);
            double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected.X1, 0.01);
            double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected.Y1, 0.01);
        }
        finally
        {
            var debugDir = Environment.GetEnvironmentVariable("GF_E2E_DEBUG_DIR");
            if (!string.IsNullOrEmpty(debugDir))
            {
                Directory.CreateDirectory(debugDir);
                foreach (var f in Directory.GetFiles(dir))
                    File.Copy(f, Path.Combine(debugDir, Path.GetFileName(f)), overwrite: true);
            }
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task UbcPdkScript_RunsWithRealSiepicCellAndProducesGds()
    {
        var python = FindGdsFactoryPython();
        if (python == null) return;   // environment not available — covered locally

        var canvas = CreateSingleComponentCanvas(out _);
        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells));

        var dir = Path.Combine(Path.GetTempPath(), $"gf-e2e-ubc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "design.py");
        var gdsPath = Path.Combine(dir, "design.gds");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script);
            var (exitCode, _, stderr) = await RunPythonAsync(python, scriptPath);
            exitCode.ShouldBe(0, $"ubcpdk script must run cleanly.\nstderr:\n{stderr}");
            File.Exists(gdsPath).ShouldBeTrue();
            new FileInfo(gdsPath).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
