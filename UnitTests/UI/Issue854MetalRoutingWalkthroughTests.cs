using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using UnitTests.Helpers;
using Xunit;
using AvaloniaFactAttribute = Avalonia.Headless.XUnit.AvaloniaFactAttribute;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #854 (curved metal routing for RF): renders the
/// routing panel now shown for a selected ELECTRICAL connection, and a routed metal
/// trace with the process metal bend radius plus the unlocked bend-radius and
/// segment-shift handles. Step-ordered PNGs + manifest.json in
/// <c>artifacts/ui-screenshots/issue-854/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue854MetalRoutingWalkthroughTests
{
    // A dark canvas with one route and a few small handles is legitimately sparse — the
    // 64×64 sample grid can step over the 6 px handle circles entirely (sparser than #791).
    private const int MinDistinctSampledColors = 3;
    private const int SampleGridSize = 64;
    private const int CanvasWidthPixels = 1000;
    private const double MetalFloorRadiusMicrometers = 40.0;

    /// <summary>Fixed world viewport (µm) so all frames are comparable.</summary>
    private static readonly Rect Viewport = new(-30, -40, 360, 260);

    /// <summary>Captures the walkthrough steps and writes the caption manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue854MetalRoutingWalkthrough()
    {
        // Opt-in like UiScreenshotTests: full headless frame captures run only when
        // screenshots are explicitly requested via UI_SHOT_DIR.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var connection = CreateRoutedMetalConnection();
        connection.IsElectrical.ShouldBeTrue();
        vm.CanvasInteraction.SelectedWaveguideConnection = new WaveguideConnectionViewModel(connection);
        var state = new CanvasInteractionState();
        var manifest = new List<object>();

        // Step 1: the routed metal trace curves with the process metal bend radius and,
        // being selected, shows the (previously optical-only) edit handles.
        CaptureScene(vm, state, connection, dir, "01-curved-metal-with-handles.png");
        manifest.Add(new
        {
            file = "01-curved-metal-with-handles.png",
            caption = "A selected metal (electrical) connection routes with curved bends at the "
                + "process metal bend radius (RF rule: at least 3× trace width) and now shows the "
                + "bend-radius circles and segment-shift diamond that were optical-only before.",
        });

        // Step 2: the Routing section is no longer hidden for electrical connections.
        CapturePanel(new ConnectionRoutingPanel(), vm, dir, "02-routing-panel-for-metal.png");
        manifest.Add(new
        {
            file = "02-routing-panel-for-metal.png",
            caption = "Selecting the metal connection also reveals the Routing section — style "
                + "presets (Auto/SBend/…) apply to electrical traces the same way as to waveguides.",
        });

        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CaptureScene(MainViewModel vm, CanvasInteractionState state,
        WaveguideConnection connection, string outputDir, string filename)
    {
        var scene = new Issue854MetalRoutingSceneControl(vm, state, connection, Viewport)
        {
            Width = CanvasWidthPixels,
            Height = CanvasWidthPixels * Viewport.Height / Viewport.Width,
        };
        CaptureWindow(new Window
        {
            Width = scene.Width,
            Height = scene.Height,
            Content = scene,
            Background = Brushes.Black,
        }, outputDir, filename);
    }

    private static void CapturePanel(Control view, object dataContext, string outputDir, string filename)
    {
        view.DataContext = dataContext;
        CaptureWindow(new Window
        {
            Width = 320,
            Height = 560,
            Content = view,
            Background = Brushes.Black,
        }, outputDir, filename);
    }

    private static void CaptureWindow(Window window, string outputDir, string filename)
    {
        window.Show();
        try
        {
            WriteableBitmap? bitmap = null;
            for (var attempt = 0; attempt < 3 && bitmap == null; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                bitmap = window.CaptureRenderedFrame();
            }
            bitmap.ShouldNotBeNull($"CaptureRenderedFrame stayed null after 3 attempts for {filename}");
            using (bitmap)
            {
                CountDistinctSampledColors(bitmap).ShouldBeGreaterThan(MinDistinctSampledColors,
                    $"Near-blank render for {filename} — likely a missing Skia setup.");
                bitmap.Save(Path.Combine(outputDir, filename));
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Electrical pin pair with a deterministic Z-path whose 90° bends use the process
    /// metal bend-radius floor — straight–bend–straight–bend–straight, so both the
    /// bend-radius corners and the middle shift handle are present (like the #791 scene).
    /// </summary>
    private static WaveguideConnection CreateRoutedMetalConnection()
    {
        double r = MetalFloorRadiusMicrometers;
        var conn = new WaveguideConnection
        {
            StartPin = CreateElectricalPin(0, 0, pinX: 50, pinY: 25, angle: 0),
            EndPin = CreateElectricalPin(250, 125, pinX: 0, pinY: 25, angle: 180),
        };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        path.Segments.Add(new BendSegment(100, r, r, 0, 90));
        path.Segments.Add(new StraightSegment(100 + r, r, 100 + r, 100, 90));
        path.Segments.Add(new BendSegment(100 + 2 * r, 100, r, 90, -90));
        path.Segments.Add(new StraightSegment(100 + 2 * r, 100 + r, 280, 100 + r, 0));
        conn.RestoreCachedPath(path);

        CAP_Core.Routing.InterconnectRouting.BendRadiusEditor
            .GetBendCorners(conn.GetPathSegments()).Count.ShouldBe(2);
        CAP_Core.Routing.InterconnectRouting.SegmentShift.SegmentShiftGeometry
            .GetHandles(conn.GetPathSegments()).ShouldHaveSingleItem();
        return conn;
    }

    private static PhysicalPin CreateElectricalPin(
        double componentX, double componentY, double pinX, double pinY, double angle)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<CAP_Core.Components.Core.Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"MetalPad_{componentX}_{componentY}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
            PhysicalX = componentX,
            PhysicalY = componentY,
        };
        return new PhysicalPin
        {
            Name = "p",
            OffsetXMicrometers = pinX,
            OffsetYMicrometers = pinY,
            AngleDegrees = angle,
            ParentComponent = component,
            LogicalPin = new Pin("p", 0, MatterType.Electricity, RectSide.Right),
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

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-854</c>, or <c>UI_SHOT_DIR/issue-854</c>.</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-854");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-854");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-854");
    }
}
