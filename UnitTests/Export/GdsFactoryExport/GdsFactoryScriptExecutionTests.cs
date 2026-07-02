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

    [Fact]
    public async Task StandaloneScript_RunsAndProducesGds()
    {
        var python = FindGdsFactoryPython();
        if (python == null) return;   // environment not available — covered locally

        var canvas = new DesignCanvasViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
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

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs));

        var dir = Path.Combine(Path.GetTempPath(), $"gf-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "design.py");
        var gdsPath = Path.Combine(dir, "design.gds");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script);

            var factory = ProcessLaunchFactory.CreateDefault();
            var (exitCode, _, stderr) = await CAP_Core.Export.PythonEnvironmentManager.UvBootstrapper
                .RunProcessAsync(factory, python, new[] { scriptPath },
                    CancellationToken.None, timeoutMs: 180_000);

            exitCode.ShouldBe(0, $"script must run cleanly.\nstderr:\n{stderr}");
            File.Exists(gdsPath).ShouldBeTrue();
            new FileInfo(gdsPath).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
