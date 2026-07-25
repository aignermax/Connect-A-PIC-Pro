using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Process;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;
using AvaloniaCanvas = Avalonia.Controls.Canvas;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #737 (one shared <see cref="PlacementPolicyContext"/> instead of
/// per-consumer duplicated funcs): drives a real <see cref="CanvasInteractionViewModel"/> wired to
/// ONE shared context and renders the placement / paste enforcement flow as step-ordered headless
/// PNGs into <c>artifacts/ui-screenshots/issue-737/</c> plus a <c>manifest.json</c> with captions.
/// Uses the same Skia headless harness as <see cref="UiScreenshotTests"/>.
/// </summary>
[Trait("Category", "UiScreenshots")]
// Renders five Skia walkthrough PNGs — too heavy for local default runs (CI covers it,
// the local runners exclude Category=Slow).
[Trait("Category", "Slow")]
public class Issue737WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    /// <summary>Renders the five walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue737Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();

        var canvas = new DesignCanvasViewModel();
        var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());

        // The single shared context (issue #737): built once, handed as one reference to every
        // placement consumer (manual canvas interaction here; AiGridService in production).
        var sharedContext = new PlacementPolicyContext(
            getActiveProcess: () => ActiveProcessSelection.ForGroup(new ProcessGroup(
                "SOI 220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
                new[] { "Demo" })),
            getProcessAgnosticPdkNames: () => new[] { "Analysis Tools" },
            resolveComponentPdkSource: _ => null);
        interaction.PlacementContext = sharedContext;

        var statusText = new TextBlock
        {
            Text = "Ready.", Margin = new Thickness(8, 4), FontSize = 13,
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap
        };
        interaction.UpdateStatus = message =>
        {
            statusText.Text = message;
            statusText.Foreground = Brushes.OrangeRed;
        };

        var componentLayer = new AvaloniaCanvas { Background = new SolidColorBrush(Color.Parse("#1E1E24")) };
        var window = BuildReplicaWindow(componentLayer, statusText);
        window.Show();
        PumpRenderLoop();

        Capture(window, dir, "01-design-locked-to-soi.png",
            "The design is locked to process 'SOI 220' via ONE shared PlacementPolicyContext that "
            + "manual placement, paste, and the AI assistant all consult.", manifest);

        interaction.SelectedTemplate = BuildTemplate("Demo");
        interaction.CanvasClicked(110, 90);
        RedrawComponents(componentLayer, canvas);
        PumpRenderLoop();
        canvas.Components.Count.ShouldBe(1);

        Capture(window, dir, "02-member-pdk-allowed.png",
            "A component from member PDK 'Demo' passes the shared context's CheckPlacement and "
            + "lands on the canvas.", manifest);

        interaction.SelectedTemplate = BuildTemplate("HHI-InP");
        interaction.CanvasClicked(330, 90);
        RedrawComponents(componentLayer, canvas);
        PumpRenderLoop();
        canvas.Components.Count.ShouldBe(1, "the foreign-PDK placement must be blocked");

        Capture(window, dir, "03-foreign-pdk-blocked.png",
            "Placing a component from foreign PDK 'HHI-InP' is rejected by the same shared "
            + "context and the block reason appears in the status line.", manifest);

        statusText.Foreground = Brushes.LightGray;
        interaction.SelectedTemplate = BuildTemplate("Analysis Tools");
        interaction.CanvasClicked(330, 90);
        RedrawComponents(componentLayer, canvas);
        PumpRenderLoop();
        canvas.Components.Count.ShouldBe(2);

        Capture(window, dir, "04-tool-pdk-allowed.png",
            "A process-agnostic tool PDK ('Analysis Tools') stays placeable because the shared "
            + "context carries the agnostic-PDK list for every consumer.", manifest);

        // Seed the clipboard with a foreign-PDK component (added directly, as if copied from an
        // older foreign-process design), then remove it so only the paste attempt remains visible.
        var foreignVm = canvas.AddComponent(CreateComponent(10, 10), "TestComp", "HHI-InP");
        canvas.Selection.SelectSingle(foreignVm);
        interaction.CopySelectedCommand.Execute(null);
        canvas.RemoveComponent(foreignVm);
        interaction.PasteSelected();
        RedrawComponents(componentLayer, canvas);
        PumpRenderLoop();
        canvas.Components.Count.ShouldBe(2, "the paste of a foreign-PDK clipboard entry must be blocked");

        Capture(window, dir, "05-paste-blocked.png",
            "Pasting clipboard content from a foreign PDK is blocked, naming the active process "
            + "read live from the shared context (PlacementContext.ActiveProcess).", manifest);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        manifest.Count.ShouldBe(5);
    }

    /// <summary>Builds the code-built replica window: header, canvas area, and status bar.</summary>
    private static Window BuildReplicaWindow(AvaloniaCanvas componentLayer, TextBlock statusText)
    {
        var header = new TextBlock
        {
            Text = "Active process: SOI 220   |   member PDK: Demo   |   tool PDK: Analysis Tools",
            Margin = new Thickness(8, 6), FontSize = 13, FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        };
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#2A2A33")), Child = statusText,
            MinHeight = 30, VerticalAlignment = VerticalAlignment.Bottom
        };
        var dock = new DockPanel { Background = new SolidColorBrush(Color.Parse("#14141A")) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(statusBar);
        dock.Children.Add(new Border
        {
            BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 0, 8, 8), Child = componentLayer
        });
        return new Window { Width = 720, Height = 320, Content = dock };
    }

    /// <summary>Redraws every placed component as a labeled rectangle, colored by PDK source.</summary>
    private static void RedrawComponents(AvaloniaCanvas layer, DesignCanvasViewModel canvas)
    {
        layer.Children.Clear();
        foreach (var vm in canvas.Components)
        {
            var color = vm.TemplatePdkSource switch
            {
                "Demo" => Color.Parse("#2E7D32"),
                "Analysis Tools" => Color.Parse("#1565C0"),
                _ => Color.Parse("#B71C1C")
            };
            var rect = new Border
            {
                Width = 150, Height = 60, Background = new SolidColorBrush(color),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = $"{vm.DisplayName}\nPDK: {vm.TemplatePdkSource}",
                    Foreground = Brushes.White, FontSize = 12, TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            AvaloniaCanvas.SetLeft(rect, vm.X);
            AvaloniaCanvas.SetTop(rect, vm.Y);
            layer.Children.Add(rect);
        }
    }

    private static ComponentTemplate BuildTemplate(string pdkSource) => new()
    {
        Name = "TestComp", Category = "Test", PdkSource = pdkSource,
        WidthMicrometers = 10, HeightMicrometers = 10,
        PinDefinitions = new[] { new PinDefinition("a", 0, 5, 180) },
        CreateSMatrix = pins =>
        {
            var ids = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            return new SMatrix(ids, new List<(Guid, double)>());
        }
    };

    private static Component CreateComponent(double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>
        {
            new("west0", 0, MatterType.Light, RectSide.Left),
            new("east0", 1, MatterType.Light, RectSide.Right),
        });
        var component = new Component(new Dictionary<int, SMatrix>(), new List<CAP_Core.Components.Core.Slider>(),
            "test_component", "", parts, 0, "TestComp", DiscreteRotation.R0)
        {
            WidthMicrometers = width, HeightMicrometers = height, PhysicalX = 0, PhysicalY = 0
        };
        return component;
    }

    /// <summary>Pumps the headless render timer and dispatcher so the frame actually paints.</summary>
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
        // Headless compositor timing (CI): the first frame may not be ready yet —
        // retry like Issue776/Issue574.
        WriteableBitmap? bitmap = null;
        for (var attempt = 0; attempt < 3 && bitmap == null; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            bitmap = window.CaptureRenderedFrame();
        }
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

    /// <summary>Resolves the walkthrough output directory (repo root's artifacts folder).</summary>
    private static string ResolveOutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-737");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-737");
    }

    /// <summary>One manifest row: PNG filename plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
