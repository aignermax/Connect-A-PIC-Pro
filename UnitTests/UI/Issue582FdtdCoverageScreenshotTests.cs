using System.Numerics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_Core.Components.Core;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #582: renders the Component Settings dialog's
/// "currently effective S-matrix" list before a recompute, after a partial-coverage
/// FDTD run (stale-wavelength warning), and after a full-coverage run.
/// Writes PNGs + manifest.json to <c>artifacts/ui-screenshots/issue-582/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue582FdtdCoverageScreenshotTests
{
    [AvaloniaFact]
    public async Task CaptureFdtdCoverageWalkthrough()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return; // opt-in: heavy headless render, only on explicit request (see UiScreenshotTests)
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);

        // 01 — before: all wavelengths come from the PDK default.
        var (vmBefore, _) = CreateConfiguredViewModel(solvedWavelengthsUm: null);
        Capture(vmBefore, Path.Combine(outputDir, "01-before-recompute.png"));

        // 02 — partial coverage: the run only produced 1550 nm → mixed effective
        // matrix, FDTD row tagged with provenance, status warns about 980/1310.
        var (vmPartial, _) = CreateConfiguredViewModel(new[] { 1.55 });
        await vmPartial.RecalculateSMatrixCommand.ExecuteAsync(null);
        vmPartial.SolverStatus.ShouldContain("Not covered");
        Capture(vmPartial, Path.Combine(outputDir, "02-partial-coverage-warning.png"));

        // 03 — full coverage (the #582 fix): the sweep hits every defined
        // wavelength, every row is FDTD-overridden, no stale warning.
        var (vmFull, _) = CreateConfiguredViewModel(new[] { 0.98, 1.31, 1.55 });
        await vmFull.RecalculateSMatrixCommand.ExecuteAsync(null);
        vmFull.SolverStatus.ShouldNotContain("Not covered");
        Capture(vmFull, Path.Combine(outputDir, "03-full-coverage.png"));

        WriteManifest(outputDir);
    }

    /// <summary>
    /// Builds a dialog ViewModel around the standard test component (defined at
    /// 980/1310/1550 nm) with an FDTD service mocked to return the given sweep.
    /// </summary>
    private static (ComponentSettingsDialogViewModel Vm, Component Component) CreateConfiguredViewModel(
        double[]? solvedWavelengthsUm)
    {
        var component = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();

        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));
        if (solvedWavelengthsUm != null)
            service.Setup(s => s.SolveAsync(
                    It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(FakeResult(solvedWavelengthsUm));

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: (_, _) => Task.FromResult<FdtdSMatrixRequest?>(new FdtdSMatrixRequest()));
        vm.Configure("comp", "comp", "DC 2-1 TE 895", new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: component,
            effectiveSMatrices: component.WaveLengthToSMatrixMap,
            effectivePins: component.GetAllPins());
        return (vm, component);
    }

    private static FdtdSMatrixResult FakeResult(double[] wavelengthsUm)
    {
        Complex[] Values() => wavelengthsUm.Select(_ => new Complex(0.93, 0.12)).ToArray();
        return new FdtdSMatrixResult
        {
            Success = true,
            Is3D = false,
            Ports = new[] { "in", "out" }, // matches the test component's pin names
            Wavelengths = wavelengthsUm,
            Entries = new[]
            {
                new FdtdSEntry { Key = "out@0,in@0", Values = Values() },
                new FdtdSEntry { Key = "in@0,out@0", Values = Values() },
            },
            EnergySumPerInput = new Dictionary<string, double> { ["in@0"] = 0.88, ["out@0"] = 0.88 },
        };
    }

    private static void Capture(ComponentSettingsDialogViewModel vm, string path)
    {
        var dialog = new CAP.Avalonia.Views.ComponentSettingsDialog { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = dialog.CaptureRenderedFrame();
        dialog.Close();
        Dispatcher.UIThread.RunJobs();

        bitmap.ShouldNotBeNull($"render miss for {Path.GetFileName(path)}");
        byte[] bytes;
        using (bitmap)
            bytes = ScreenshotArtifacts.SavePng(bitmap!, path);
        bytes.Length.ShouldBeGreaterThan(0);
    }

    private static void WriteManifest(string outputDir)
    {
        const string manifest = """
        [
          {"file": "01-before-recompute.png", "caption": "Before recompute: all three wavelengths (980/1310/1550 nm) are tagged 'PDK Default'."},
          {"file": "02-partial-coverage-warning.png", "caption": "A run covering only 1550 nm: that row is tagged 'Override active — FDTD Meep 2D' and the status warns that 980/1310 nm are still PDK default."},
          {"file": "03-full-coverage.png", "caption": "Fixed recompute sweeps the component's own wavelengths: every row is FDTD-overridden and no stale warning appears."}
        ]
        """;
        ScreenshotArtifacts.WriteText(Path.Combine(outputDir, "manifest.json"), manifest);
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-582</c> (or <c>UI_SHOT_DIR/issue-582</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-582");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-582");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-582");
    }
}
