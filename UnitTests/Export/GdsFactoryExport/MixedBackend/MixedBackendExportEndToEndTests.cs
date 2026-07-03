using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>
/// End-to-end acceptance for issue #646: a design mixing a Nazca-backend and a
/// gdsfactory-backend override exports to ONE GDS in which the Nazca-rendered custom
/// geometry lands at the exact position the single-backend Nazca reference export puts
/// it. Requires a nazca-capable Python (system) and the gf-groundtruth env (see
/// <see cref="GdsFactoryScriptExecutionTests"/>); skips silently when either is missing.
/// </summary>
public class MixedBackendExportEndToEndTests
{
    private const string OverrideRawCode =
        "import nazca as nd\n"
        + "with nd.Cell(name='ovr_box') as _c:\n"
        + "    nd.Polygon(points=[(0,0),(10,0),(10,5),(0,5)], layer=1).put(0, 0)\n"
        + "def component():\n"
        + "    return _c\n";

    private static string? FindGdsFactoryPython() => new[]
    {
        Path.Combine(Path.GetTempPath(), "gf-groundtruth", "Scripts", "python.exe"),
        Path.Combine(Path.GetTempPath(), "gf-groundtruth", "bin", "python"),
    }.FirstOrDefault(File.Exists);

    private static async Task<string?> FindNazcaPythonAsync()
    {
        foreach (var cmd in new[] { "python3", "python" })
        {
            var resolved = ProcessLaunchFactory.CreateDefault().ResolveExecutable(cmd);
            if (resolved == null) continue;
            var (exit, _, _) = await RunPythonAsync(resolved, "-c", "import nazca");
            if (exit == 0) return resolved;
        }
        return null;
    }

    private static async Task<(int exitCode, string output, string stderr)> RunPythonAsync(
        string python, params string[] args) =>
        await CAP_Core.Export.PythonEnvironmentManager.UvBootstrapper.RunProcessAsync(
            ProcessLaunchFactory.CreateDefault(), python, args,
            CancellationToken.None, timeoutMs: 300_000);

    private static DesignCanvasViewModel CreateMixedCanvas(
        out CAP_Core.Components.Core.Component nazcaComp,
        out Dictionary<string, NazcaCodeOverride> overrides)
    {
        var canvas = new DesignCanvasViewModel();

        nazcaComp = TestComponentFactory.CreateBasicComponent();
        nazcaComp.Identifier = "NazcaOvr";
        nazcaComp.NazcaFunctionName = "ebeam_y_1550";
        nazcaComp.PhysicalX = 100;
        nazcaComp.PhysicalY = 50;
        canvas.AddComponent(nazcaComp, "NazcaOvr");

        var plain = TestComponentFactory.CreateBasicComponent();
        plain.Identifier = "Y1";
        plain.NazcaFunctionName = "ebeam_y_1550";
        plain.PhysicalX = 300;
        plain.PhysicalY = 50;
        canvas.AddComponent(plain, "Y1");

        var ovr = new NazcaCodeOverride { RawCode = OverrideRawCode, Backend = OverrideBackend.Nazca };
        ovr.SetOverrideGeometry(width: 10, height: 5, bboxXMin: 0, bboxYMax: 5);
        overrides = new Dictionary<string, NazcaCodeOverride> { ["NazcaOvr"] = ovr };
        return canvas;
    }

    /// <summary>Prints every layer-1 polygon bbox of the flattened design cell.</summary>
    private static string BboxProbe(string gdsPath, string cellName) =>
        $"import gdstk; lib = gdstk.read_gds(r'{gdsPath}'); "
        + $"tops = [c for c in lib.cells if c.name.startswith('{cellName}')]; "
        + "top = tops[0].flatten(); "
        + "print('\\n'.join(f'{p.bounding_box()[0][0]:.2f};{p.bounding_box()[0][1]:.2f};"
        + "{p.bounding_box()[1][0]:.2f};{p.bounding_box()[1][1]:.2f}' for p in top.polygons))";

    private static bool ContainsBbox(string probeOutput, double x0, double y0, double x1, double y1) =>
        probeOutput.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(';'))
            .Where(p => p.Length == 4)
            .Any(p => Math.Abs(double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture) - x0) < 0.01
                   && Math.Abs(double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) - y0) < 0.01
                   && Math.Abs(double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) - x1) < 0.01
                   && Math.Abs(double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) - y1) < 0.01);

    [Fact]
    public async Task MixedExport_BothBackendsLandInOneGds_AlignedWithNazcaReference()
    {
        var gfPython = FindGdsFactoryPython();
        var nazcaPython = await FindNazcaPythonAsync();
        if (gfPython == null || nazcaPython == null) return;   // envs not available — covered locally

        var canvas = CreateMixedCanvas(out var nazcaComp, out var overrides);
        var dir = Path.Combine(Path.GetTempPath(), $"mixed-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Phase 1: Nazca part (run with the nazca-capable interpreter).
            var partScript = Path.Combine(dir, "design_nazca_part.py");
            await File.WriteAllTextAsync(partScript,
                new NazcaPartialExporter().Export(canvas, overrides));
            var (partExit, _, partErr) = await RunPythonAsync(nazcaPython, partScript);
            partExit.ShouldBe(0, $"Nazca part must run cleanly.\nstderr:\n{partErr}");

            // Phase 2: gdsfactory host merges the part GDS (run with the gf interpreter).
            var hostScript = Path.Combine(dir, "design.py");
            var merge = new NazcaGdsMerge(new HashSet<string> { "NazcaOvr" }, "design_nazca_part.gds");
            await File.WriteAllTextAsync(hostScript, new GdsFactoryExporter().Export(
                canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells), overrides, merge));
            var (hostExit, _, hostErr) = await RunPythonAsync(gfPython, hostScript);
            hostExit.ShouldBe(0, $"gdsfactory host must run cleanly.\nstderr:\n{hostErr}");

            var finalGds = Path.Combine(dir, "design.gds");
            File.Exists(finalGds).ShouldBeTrue();

            // The override geometry must land on the component's grid rectangle (#561 contract)
            // in the FINAL composed GDS...
            var expected = (
                X0: nazcaComp.PhysicalX, Y0: -(nazcaComp.PhysicalY + 5),
                X1: nazcaComp.PhysicalX + 10, Y1: -nazcaComp.PhysicalY);
            var (probeExit, mixedOut, probeErr) = await RunPythonAsync(
                gfPython, "-c", BboxProbe(finalGds, "ConnectAPIC_Design"));
            probeExit.ShouldBe(0, $"bbox probe must run.\nstderr:\n{probeErr}");
            ContainsBbox(mixedOut, expected.X0, expected.Y0, expected.X1, expected.Y1)
                .ShouldBeTrue($"override geometry missing at expected position.\npolygons:\n{mixedOut}");

            // ...and at the same position the single-backend Nazca reference export puts it.
            var refScript = Path.Combine(dir, "reference.py");
            await File.WriteAllTextAsync(refScript,
                new CAP.Avalonia.Services.SimpleNazcaExporter().Export(canvas, overrides: overrides));
            var (refExit, _, refErr) = await RunPythonAsync(nazcaPython, refScript);
            refExit.ShouldBe(0, $"Nazca reference must run cleanly.\nstderr:\n{refErr}");
            var (refProbeExit, refOut, _) = await RunPythonAsync(
                gfPython, "-c", BboxProbe(Path.Combine(dir, "reference.gds"), "ConnectAPIC_Design"));
            refProbeExit.ShouldBe(0);
            ContainsBbox(refOut, expected.X0, expected.Y0, expected.X1, expected.Y1)
                .ShouldBeTrue($"reference export disagrees with the placement contract.\npolygons:\n{refOut}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
