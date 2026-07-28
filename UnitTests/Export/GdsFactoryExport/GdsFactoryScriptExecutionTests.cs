using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export.GdsFactoryExport;

/// <summary>
/// End-to-end: runs the generated gdsfactory script with a real Python and asserts a
/// GDS file is produced. Skips silently when no gdsfactory-capable interpreter is
/// available (CI without the ground-truth env). Locally, create one via
/// <c>%LOCALAPPDATA%\Lunima\tools\uv.exe venv %TEMP%\gf-groundtruth --python 3.12</c>
/// + <c>uv pip install gdsfactory ubcpdk</c>.
/// </summary>
[Trait("Category", "Slow")]
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

    /// <summary>
    /// Locates a Python venv that can <c>import cspdk.sin300</c> — a Lunima managed env
    /// (%LOCALAPPDATA%\Lunima\envs\*) or the %TEMP%\gf-groundtruth env with cspdk installed.
    /// Returns null when none is present (CI), so the CornerStone integration test skips.
    /// </summary>
    private static string? FindCspdkPython()
    {
        var roots = new List<string>();
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (Directory.Exists(envs))
            roots.AddRange(Directory.GetDirectories(envs));
        roots.Add(Path.Combine(Path.GetTempPath(), "gf-groundtruth"));

        foreach (var root in roots)
        {
            if (!HasCspdk(root)) continue;
            foreach (var rel in new[]
                     {
                         Path.Combine("Scripts", "python.exe"),
                         Path.Combine("bin", "python"),
                     })
            {
                var py = Path.Combine(root, rel);
                if (File.Exists(py)) return py;
            }
        }
        return null;
    }

    private static bool HasCspdk(string envRoot)
    {
        if (Directory.Exists(Path.Combine(envRoot, "Lib", "site-packages", "cspdk")))
            return true;   // Windows venv layout
        var lib = Path.Combine(envRoot, "lib");
        return Directory.Exists(lib)
            && Directory.GetDirectories(lib)
                .Any(d => Directory.Exists(Path.Combine(d, "site-packages", "cspdk")));
    }

    private static DesignCanvasViewModel CreateConnectedCornerStoneSinCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var a = CreateSinComponent("A", "cspdk.sin300.mmi1x2", 0, 0);
        var b = CreateSinComponent("B", "cspdk.sin300.straight", 60, 0);
        canvas.AddComponent(a, "SiN A");
        canvas.AddComponent(b, "SiN B");
        var connection = new CAP_Core.Components.Connections.WaveguideConnection
        {
            StartPin = a.PhysicalPins[0],
            EndPin = b.PhysicalPins[0],
        };
        // A blocked/invalid/routeless connection is skipped from export — give it a real
        // routed path so the generated script actually contains the waveguide connection
        // this test's name promises.
        var path = new CAP_Core.Routing.RoutedPath();
        path.Segments.Add(new CAP_Core.Routing.StraightSegment(0, 0, 1, 0, 0));
        connection.RestoreCachedPath(path);
        canvas.Connections.Add(new CAP.Avalonia.ViewModels.Canvas.WaveguideConnectionViewModel(connection));
        return canvas;
    }

    private static CAP_Core.Components.Core.Component CreateSinComponent(
        string id, string gdsFactoryFunction, double x, double y)
    {
        var c = TestComponentFactory.CreateBasicComponent();
        c.Identifier = id;
        c.NazcaFunctionName = "";
        c.GdsFactoryFunction = gdsFactoryFunction;
        c.GdsFactoryRoutingCrossSection = "xs_nc";
        c.PhysicalX = x;
        c.PhysicalY = y;
        c.RotationDegrees = 0;
        c.PhysicalPins.Add(new CAP_Core.Components.Core.PhysicalPin
        {
            Name = "o1",
            ParentComponent = c,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 180,
        });
        return c;
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
    public async Task CornerStoneSinDesign_WithWaveguideConnection_RunsAndProducesGds()
    {
        var python = FindCspdkPython();
        if (python == null) return;   // no cspdk env — covered locally

        var canvas = CreateConnectedCornerStoneSinCanvas();
        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs));

        var dir = Path.Combine(Path.GetTempPath(), $"gf-e2e-sin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "design.py");
        var gdsPath = Path.Combine(dir, "design.gds");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script);
            var (exitCode, _, stderr) = await RunPythonAsync(python, scriptPath);
            exitCode.ShouldBe(0,
                $"CornerStone SiN export with a routed waveguide must run cleanly.\nstderr:\n{stderr}");
            File.Exists(gdsPath).ShouldBeTrue();
            new FileInfo(gdsPath).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
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
