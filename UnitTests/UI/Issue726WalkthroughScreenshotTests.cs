using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.Views;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #726 (Fabrication Process editor can switch the design's
/// process; the active process is preselected in the preset dropdown): renders the dialog
/// flow as step-ordered headless PNGs into <c>artifacts/ui-screenshots/issue-726/</c> plus a
/// <c>manifest.json</c> with one-sentence captions. Uses the same Skia headless harness as
/// <see cref="UiScreenshotTests"/>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue726WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    /// <summary>Renders the four walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue726Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var applied = new List<ActiveProcessSelection>();
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
        {
            ApplyActiveProcess = applied.Add,
        };

        var pdks = new List<PdkDraft> { SoiDraft("SiEPIC EBeam"), SiNDraft("CornerStone SiN") };
        var active = new ActiveProcessSelection(
            "SOI-220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI-220"),
            new List<string> { "SiEPIC EBeam" }, IsPlayground: false);
        vm.ShowActiveProcess(active, pdks);

        // Wider than the 820 px production default so the full button row (…preset dropdown,
        // new "Set as design process", Save, process name box) is visible in the capture.
        var window = new ProcessManagementWindow { Width = 1280, Height = 640, DataContext = vm };
        window.Show();
        PumpRenderLoop();

        Capture(window, dir, "01-active-process-preselected.png",
            "Opening the Fabrication Process editor now preselects the design's active process "
            + "(SiEPIC EBeam / SOI-220) in the preset dropdown instead of an empty picker, next "
            + "to the new 'Set as design process' button.", manifest);

        vm.SelectedPreset = vm.AvailablePresets.First(p => p.Name == "CornerStone SiN");
        PumpRenderLoop();

        Capture(window, dir, "02-different-preset-picked.png",
            "Picking another preset (CornerStone SiN) loads its layer stack, cross-sections and "
            + "materials into the editor, ready to become the design's process.", manifest);

        vm.PlacedComponentCountProvider = () => 3;
        vm.SetAsDesignProcessCommand.Execute(null);
        PumpRenderLoop();

        Capture(window, dir, "03-guard-components-placed.png",
            "With components on the canvas, 'Set as design process' refuses and explains that "
            + "placed components carry S-matrices of the current process (one design = one "
            + "process).", manifest);

        vm.PlacedComponentCountProvider = () => 0;
        vm.SetAsDesignProcessCommand.Execute(null);
        PumpRenderLoop();

        Capture(window, dir, "04-design-process-switched.png",
            "On an empty canvas the same click applies the selection: the design is now locked "
            + "to SiN-300 and the status confirms the switch.", manifest);

        applied.ShouldHaveSingleItem().DisplayName.ShouldBe("SiN-300");

        window.Close();
        Dispatcher.UIThread.RunJobs();

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);

        manifest.Count.ShouldBe(4);
    }

    /// <summary>A PDK draft declaring the SOI-220 process used as the design's active process.</summary>
    private static PdkDraft SoiDraft(string name) => new()
    {
        Name = name,
        DefaultWavelengthNm = 1550,
        Process = new ProcessDefinition
        {
            Name = "SOI-220",
            CoreThicknessNm = 220,
            Layers = { new ProcessLayer { Name = "WAVEGUIDE", Layer = 1, Description = "Si waveguide core" } },
            Xsections = { new ProcessXsection { Name = "strip", WidthUm = 0.5, MinRadiusUm = 5, Description = "Single-mode strip waveguide" } },
            Materials =
            {
                new ProcessMaterial { Name = "Si", Role = "core" },
                new ProcessMaterial { Name = "SiO2", Role = "cladding" },
            },
        },
    };

    /// <summary>A second, incompatible PDK draft (SiN-300) the user switches the design to.</summary>
    private static PdkDraft SiNDraft(string name) => new()
    {
        Name = name,
        DefaultWavelengthNm = 1550,
        Process = new ProcessDefinition
        {
            Name = "SiN-300",
            CoreThicknessNm = 300,
            Layers = { new ProcessLayer { Name = "SiN_CORE", Layer = 203, Description = "SiN waveguide core" } },
            Xsections = { new ProcessXsection { Name = "sin_strip", WidthUm = 1.2, MinRadiusUm = 60, Description = "SiN strip waveguide" } },
            Materials =
            {
                new ProcessMaterial { Name = "SiN", Role = "core" },
                new ProcessMaterial { Name = "SiO2", Role = "cladding" },
            },
        },
    };

    /// <summary>
    /// Pumps the headless render timer and dispatcher a few times so bindings and layout
    /// actually paint before capture.
    /// </summary>
    private static void PumpRenderLoop()
    {
        for (int i = 0; i < 5; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Captures the shown window to a PNG, fails on a near-blank frame, records the caption.</summary>
    private static void Capture(
        ProcessManagementWindow window, string dir, string filename, string caption,
        List<ManifestEntry> manifest)
    {
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
            return Path.Combine(envDir, "issue-726");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-726");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-726");
    }

    /// <summary>One manifest row: screenshot file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
