using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Settings;
using CAP.Avalonia.Views;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for the per-connection routing panel: renders its user flow
/// (select connection → pick a routing style, which reshapes the visible curve) and the
/// Settings → Interconnects page. Writes step-ordered PNGs + manifest.json to
/// <c>artifacts/ui-screenshots/issue-574/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue574RoutingWalkthroughTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    /// <summary>Captures the four walkthrough steps and writes the caption manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue574RoutingWalkthrough()
    {
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var connection = CreateRoutedConnectionWithBends();
        var manifest = new List<object>();

        // Step 1: selecting a waveguide connection reveals the new Routing section.
        vm.CanvasInteraction.SelectedWaveguideConnection = new WaveguideConnectionViewModel(connection);
        Capture(new ConnectionRoutingPanel(), vm, 320, 560, outputDir, "01-connection-selected.png");
        manifest.Add(new
        {
            file = "01-connection-selected.png",
            caption = "Selecting a waveguide connection shows the new Routing section with style, width, bend radius, freeze toggle and per-bend radius editor.",
        });

        // Step 2: the user picks an explicit routing style; the connection's Type updates and
        // the visible canvas curve is reshaped into the matching primitive geometry.
        var routing = vm.BottomPanel.ConnectionRouting;
        routing.SelectedStyle = WaveguideType.SBend;
        connection.Type.ShouldBe(WaveguideType.SBend);
        Capture(new ConnectionRoutingPanel(), vm, 320, 560, outputDir, "02-style-selected.png");
        manifest.Add(new
        {
            file = "02-style-selected.png",
            caption = "Choosing the SBend style applies immediately; width and bend radius come automatically from the interconnect defaults — no manual number fields.",
        });

        // Step 4: the new Settings → Interconnects page with global export defaults.
        var prefs = new UserPreferencesService(
            Path.Combine(Path.GetTempPath(), $"cap-walkthrough-prefs-{Guid.NewGuid()}.json"));
        var settingsVm = new SettingsWindowViewModel(new ISettingsPage[]
        {
            new InterconnectSettingsPage(new InterconnectSettingsViewModel(prefs)),
        });
        var settingsWindow = new SettingsWindow { DataContext = settingsVm };
        CaptureWindow(settingsWindow, outputDir, "04-settings-interconnects.png");
        manifest.Add(new
        {
            file = "04-settings-interconnects.png",
            caption = "The new Settings → Interconnects page edits the global export defaults: waveguide width, bend radius and optional GDS layer.",
        });

        File.WriteAllText(
            Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Capture(Control view, object dataContext, int width, int height, string outputDir, string filename)
    {
        view.DataContext = dataContext;
        var window = new Window { Width = width, Height = height, Content = view, Background = global::Avalonia.Media.Brushes.Black };
        CaptureWindow(window, outputDir, filename);
    }

    private static void CaptureWindow(Window window, string outputDir, string filename)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        bitmap.ShouldNotBeNull($"CaptureRenderedFrame returned null for {filename}");
        var path = Path.Combine(outputDir, filename);
        using (bitmap)
        {
            CountDistinctSampledColors(bitmap).ShouldBeGreaterThan(MinDistinctSampledColors,
                $"Near-blank render for {filename} — likely a missing Skia setup.");
            bitmap.Save(path);
        }
    }

    /// <summary>
    /// Creates a connection with pins and a deterministic routed path containing one
    /// editable 90° bend between two straights (straight → bend r=10 → straight),
    /// so the Apply-Bend-Radius walkthrough step succeeds reproducibly.
    /// </summary>
    private static WaveguideConnection CreateRoutedConnectionWithBends()
    {
        var startComponent = CreateTestComponent(0, 0);
        var endComponent = CreateTestComponent(300, 200);

        var conn = new WaveguideConnection
        {
            StartPin = new PhysicalPin
            {
                Name = "output",
                OffsetXMicrometers = 50,
                OffsetYMicrometers = 25,
                AngleDegrees = 0,
                ParentComponent = startComponent,
            },
            EndPin = new PhysicalPin
            {
                Name = "input",
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 25,
                AngleDegrees = 180,
                ParentComponent = endComponent,
            },
        };

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        path.Segments.Add(new BendSegment(50, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(60, 10, 60, 60, 90));
        conn.RestoreCachedPath(path);

        CAP_Core.Routing.InterconnectRouting.BendRadiusEditor
            .CountBends(conn.GetPathSegments()!).ShouldBeGreaterThan(0);
        return conn;
    }

    private static Component CreateTestComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<CAP_Core.Components.Core.Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"WalkthroughComponent_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
            PhysicalX = x,
            PhysicalY = y,
        };
    }

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

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-574</c>, or <c>UI_SHOT_DIR/issue-574</c>.</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-574");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-574");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-574");
    }
}
