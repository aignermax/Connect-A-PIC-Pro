using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP.Avalonia.ViewModels.Settings;
using CAP.Avalonia.ViewModels.Solvers;
using CAP.Avalonia.Views;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #692 (Tidy3D cloud FDTD backend): renders the changed UI flow
/// as step-ordered headless PNGs into <c>artifacts/ui-screenshots/issue-692/</c> plus a
/// <c>manifest.json</c> with one-sentence captions. All solver services are mocked so no
/// Docker, Python, or Tidy3D account is required — the test only exercises the Avalonia views.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue692WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    private const string MissingKeyHint =
        "No Tidy3D API key is configured. Add it under Settings → Tidy3D Cloud "
        + "(get one from tidy3d.simulation.cloud → Account).";

    /// <summary>Test double that is both a solver and a cost estimator, like the real Tidy3D backend.</summary>
    public abstract class Tidy3dLikeService : IFdtdSMatrixService, IFdtdCostEstimator
    {
        /// <inheritdoc/>
        public abstract Task<FdtdSMatrixResult> SolveAsync(
            FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<FdtdCostEstimate> EstimateCostAsync(
            FdtdSMatrixRequest request, CancellationToken ct = default);
    }

    /// <summary>Renders the four walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue692Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var prefsPath = Path.Combine(Path.GetTempPath(), $"issue692_prefs_{Guid.NewGuid():N}.json");
        var manifest = new List<ManifestEntry>();
        try
        {
            await CaptureComponentSettingsSteps(dir, prefsPath, manifest);
            CaptureTidy3dSettingsPageStep(dir, prefsPath, manifest);
        }
        finally
        {
            if (File.Exists(prefsPath)) File.Delete(prefsPath);
        }

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);

        manifest.Count.ShouldBe(4);
    }

    /// <summary>Steps 1–3: backend picker, missing-key hint, and cloud cost confirmation.</summary>
    private static async Task CaptureComponentSettingsSteps(
        string dir, string prefsPath, List<ManifestEntry> manifest)
    {
        var tidy3d = new Mock<Tidy3dLikeService>();
        var backendSelection = new FdtdBackendSelectionViewModel(new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>
            {
                [FdtdBackendType.MeepDocker] = new Mock<IFdtdSMatrixService>().Object,
                [FdtdBackendType.Tidy3D] = tidy3d.Object,
            },
            new UserPreferencesService(prefsPath)));

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdRequestFactory: (_, _) => Task.FromResult<FdtdSMatrixRequest?>(new FdtdSMatrixRequest()),
            backendSelection: backendSelection);
        vm.Configure("comp", "comp", "ebeam_y_1550 (instance)",
            new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        var dialog = new ComponentSettingsDialog { DataContext = vm, Width = 700, Height = 620 };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Capture(dialog, dir, "01-dialog-backend-picker-meep.png",
            "Component Settings now shows an FDTD backend picker next to Recalculate, "
            + "defaulting to the free local Meep (Docker) backend.", manifest);

        // Step 2: switch to Tidy3D while no API key is configured — the picker stays
        // usable but explains the missing prerequisite instead of failing later.
        tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(FdtdAvailability.Unavailable(MissingKeyHint));
        backendSelection.SelectedBackendName = "Tidy3D (cloud)";
        await backendSelection.CheckAvailabilityAsync();
        Dispatcher.UIThread.RunJobs();

        Capture(dialog, dir, "02-tidy3d-selected-missing-key-hint.png",
            "Selecting Tidy3D (cloud) without an API key shows an actionable hint "
            + "pointing to Settings → Tidy3D Cloud.", manifest);

        // Step 3: with the backend ready, Recalculate pauses at the cost estimate —
        // nothing is submitted to the cloud until the user explicitly confirms.
        tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(FdtdAvailability.Available("tidy3d 2.7 ready"));
        tidy3d.Setup(s => s.EstimateCostAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new FdtdCostEstimate
              {
                  Success = true,
                  EstimatedCredits = 0.65,
                  SimulationCount = 2
              });
        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.IsAwaitingCloudConfirmation.ShouldBeTrue();
        Capture(dialog, dir, "03-cloud-cost-confirmation.png",
            "Recalculating via Tidy3D first shows the estimated FlexCredit cost and waits "
            + "for explicit confirmation before submitting the cloud job.", manifest);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Step 4: the new Tidy3D Cloud settings page where the shared API key lives.</summary>
    private static void CaptureTidy3dSettingsPageStep(
        string dir, string prefsPath, List<ManifestEntry> manifest)
    {
        var tidy3dVm = new Tidy3dSettingsViewModel(new UserPreferencesService(prefsPath))
        {
            ApiKey = "t3d-example-api-key"
        };
        var settingsVm = new SettingsWindowViewModel(new ISettingsPage[]
        {
            new Tidy3dSettingsPage(tidy3dVm)
        });

        var window = new SettingsWindow { DataContext = settingsVm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Capture(window, dir, "04-settings-tidy3d-cloud-page.png",
            "The new Settings → Tidy3D Cloud page stores the API key shared by the FDTD "
            + "S-matrix backend and the Tidy3D mode solver.", manifest);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Captures the shown window to a PNG, fails on a near-blank frame, records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        Dispatcher.UIThread.RunJobs();
        var bitmap = window.CaptureRenderedFrame();
        bitmap.ShouldNotBeNull($"CaptureRenderedFrame returned null for {filename}");

        var path = Path.Combine(dir, filename);
        int distinctColors;
        using (bitmap)
        {
            distinctColors = CountDistinctSampledColors(bitmap);
            bitmap.Save(path);
        }

        distinctColors.ShouldBeGreaterThan(MinDistinctSampledColors,
            $"Near-blank render — only {distinctColors} distinct sampled colors in {filename}.");
        manifest.Add(new ManifestEntry(filename, caption));
    }

    /// <summary>Samples a grid of pixels and counts distinct ARGB values (blank-frame guard).</summary>
    private static int CountDistinctSampledColors(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        int width = fb.Size.Width;
        int height = fb.Size.Height;
        if (width <= 0 || height <= 0) return 0;

        int stepX = Math.Max(1, width / SampleGridSize);
        int stepY = Math.Max(1, height / SampleGridSize);
        var colors = new HashSet<int>();
        for (int y = 0; y < height; y += stepY)
        {
            var rowAddr = fb.Address + y * fb.RowBytes;
            for (int x = 0; x < width; x += stepX)
                colors.Add(Marshal.ReadInt32(rowAddr, x * 4));
        }
        return colors.Count;
    }

    /// <summary>Repo-root walkthrough output directory (env override: <c>UI_SHOT_DIR</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-692");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-692");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-692");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
