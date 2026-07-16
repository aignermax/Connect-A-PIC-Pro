using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Process;
using CAP.Avalonia.Views;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #696 ("Use preset" sets the design's fabrication process):
/// renders the Fabrication Process window's UI flow as step-ordered headless PNGs into
/// <c>artifacts/ui-screenshots/issue-696/</c> plus a <c>manifest.json</c> with one-sentence
/// captions. Uses the same Skia headless harness as <see cref="UiScreenshotTests"/>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue696WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    // Slightly wider than the window's default 820 so the trailing "Process:" name box in the
    // toolbar is not clipped at the right edge of the capture.
    private const int WindowWidth = 980;

    /// <summary>Renders the four walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue696Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var preset = SinPreset();

        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        vm.SetAvailablePresets(new[] { preset });
        vm.UseAsDesignProcess = (_, _) => { };
        vm.CommitOverrides = (_, _) => { };

        var window = new ProcessManagementWindow { DataContext = vm, Width = WindowWidth };
        window.Show();
        PumpRenderLoop();

        Capture(window, dir, "01-use-preset-dropdown.png",
            "The Fabrication Process window offers the renamed 'Use preset ▾' dropdown, whose "
            + "tooltip now explains USE semantics: the pick becomes the design's process.", manifest);

        vm.SelectedPreset = preset;
        PumpRenderLoop();

        Capture(window, dir, "02-preset-in-use-banner.png",
            "Picking a preset sets it as the design's active process — a blue banner confirms "
            + "'Using preset … — unchanged' and the editor shows the preset's layer stack.", manifest);

        // Edit through the rendered TextBox (as a user would), then refresh — this is what the
        // window's LostFocus handler triggers after a field edit commits.
        var widthBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == "1.2");
        widthBox.Text = "1.5";
        vm.RefreshOverrideSummary();
        PumpRenderLoop();

        Capture(window, dir, "03-design-only-override.png",
            "Editing a field (waveguide width 1.2 → 1.5 µm) commits a design-only override — the "
            + "banner reports '1 property overridden' and the PDK file on disk stays unchanged.", manifest);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        // Reopen: a fresh dialog restores the stored preset + overrides from the design.
        var reopenedVm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        var selection = new ActiveProcessSelection(
            "CornerStone SiN 300nm", ProcessFingerprintFactory.From(preset),
            new List<string> { preset.Name }, IsPlayground: false);
        var storedOverrides = new List<ProcessPropertyOverrideData>
        {
            new()
            {
                Section = ProcessPropertyOverrideData.XsectionsSection, RowName = "xs_nc",
                Property = nameof(ProcessXsection.WidthUm), Value = "1.5",
            },
        };
        reopenedVm.ShowActiveProcess(selection, new List<PdkDraft> { preset }, preset.Name, storedOverrides);

        var reopenedWindow = new ProcessManagementWindow { DataContext = reopenedVm, Width = WindowWidth };
        reopenedWindow.Show();
        PumpRenderLoop();

        Capture(reopenedWindow, dir, "04-reopened-state-restored.png",
            "Reopening the dialog restores the persisted state: the banner shows the preset with "
            + "its override count and the editor shows the effective (overridden) values.", manifest);

        reopenedWindow.Close();
        Dispatcher.UIThread.RunJobs();

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);

        manifest.Count.ShouldBe(4);
    }

    /// <summary>A representative bundled-PDK preset with a small but realistic process.</summary>
    private static PdkDraft SinPreset() => new()
    {
        Name = "CornerStone SiN",
        DefaultWavelengthNm = 1550,
        Process = new ProcessDefinition
        {
            Name = "CornerStone SiN 300nm",
            CoreThicknessNm = 300,
            Layers =
            {
                new ProcessLayer { Name = "NITRIDE", Layer = 203, Datatype = 0, Description = "SiN waveguide core" },
                new ProcessLayer { Name = "METAL", Layer = 39, Datatype = 0, Description = "Electrical wiring" },
            },
            Xsections =
            {
                new ProcessXsection
                {
                    Name = "xs_nc", Kind = XsectionKind.Optical, WidthUm = 1.2,
                    MinRadiusUm = 40, RecommendedRadiusUm = 60, Description = "SiN strip waveguide",
                },
            },
            Materials =
            {
                new ProcessMaterial { Name = "SiN", Role = "core" },
                new ProcessMaterial { Name = "SiO2", Role = "cladding" },
            },
        },
    };

    /// <summary>
    /// Pumps the headless render timer and dispatcher a few times so bindings and layout
    /// settle before capture.
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
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
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
            return Path.Combine(envDir, "issue-696");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-696");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-696");
    }

    /// <summary>One manifest row: the PNG filename plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
