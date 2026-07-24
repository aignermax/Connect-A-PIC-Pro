using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Views;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Tiles;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual diagnostic for arc-aware collision handling (user bug report, two symptoms):
/// (a) a manually enlarged bend radius freezes the route; dropping a component onto the
/// arc's belly must unfreeze and re-route instead of leaving the waveguide through the
/// component; (b) a Cobra-styled connection must act as an obstacle for its Auto sibling,
/// which has to route around it instead of crossing. PNGs and a findings text file go to
/// <c>UI_SHOT_DIR/arc-obstacle-collision/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class ArcObstacleCollisionDiagnosticTests
{
    private const int CanvasWidth = 1100;
    private const int CanvasHeight = 800;
    private const int CaptureAttempts = 3;

    /// <summary>Radii tried (descending) for the manual handle edit; the first that fits wins.</summary>
    private static readonly double[] HandleRadiiMicrometers = { 140, 120, 100, 80, 60, 40 };

    /// <summary>Captures both repro scenarios and writes the findings file.</summary>
    [AvaloniaFact]
    public async Task CaptureArcCollisionScenarios()
    {
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "arc-obstacle-collision");
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*"))
            File.Delete(stale);

        var findings = new List<string>();
        await CaptureFrozenArcBellyScenario(outputDir, findings);
        await CaptureCobraSiblingScenario(outputDir, findings);
        await CaptureFlatCobraSiblingScenario(outputDir, findings);
        File.WriteAllLines(Path.Combine(outputDir, "findings.txt"), findings);
    }

    /// <summary>
    /// Scenario (a): auto route between two couplers, first bend enlarged via the handle
    /// editor (freezes the route), then a component is dropped onto the arc belly and the
    /// routes are recalculated. Records whether the route still pierces the component.
    /// </summary>
    private static async Task CaptureFrozenArcBellyScenario(string outputDir, List<string> findings)
    {
        var canvas = new DesignCanvasViewModel();
        var left = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("left");
        left.PhysicalX = 60;
        left.PhysicalY = 60;
        var right = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("right");
        right.PhysicalX = 560;
        right.PhysicalY = 360;
        canvas.AddComponent(left);
        canvas.AddComponent(right);

        var (window, _) = ShowWindow(canvas);
        try
        {
            var connVm = await canvas.ConnectPinsAsync(Pin(left, "east0"), Pin(right, "west0"));
            connVm.ShouldNotBeNull();
            var conn = connVm!.Connection;
            await canvas.RecalculateRoutesAsync();

            var corners = BendRadiusEditor.GetBendCorners(conn.GetPathSegments());
            corners.ShouldNotBeEmpty("the auto route must expose a resizable bend");
            double? applied = null;
            foreach (var radius in HandleRadiiMicrometers)
            {
                if (BendRadiusEditor.TryApplyOverride(conn, corners[0].BendIndex, radius, out _))
                {
                    applied = radius;
                    break;
                }
            }
            applied.ShouldNotBeNull("one of the handle radii must fit the route");
            findings.Add($"[belly] applied handle radius: {applied} µm; frozen={conn.IsRouteFrozen}");
            Capture(window, canvas, Path.Combine(outputDir, "belly-1-frozen-big-radius.png"));

            var belly = ArcBellyOf(conn, corners[0].BendIndex);
            findings.Add($"[belly] arc belly at ({belly.X:F1}, {belly.Y:F1})");
            var blocker = CreateBlockerComponent(belly.X, belly.Y, sizeMicrometers: 50);
            canvas.AddComponent(blocker);
            await canvas.RecalculateRoutesAsync();

            bool pierces = PathIntersectionDetector.IntersectsRectangle(
                conn.RoutedPath!,
                blocker.PhysicalX + 0.5, blocker.PhysicalY + 0.5,
                blocker.PhysicalX + blocker.WidthMicrometers - 0.5,
                blocker.PhysicalY + blocker.HeightMicrometers - 0.5);
            findings.Add($"[belly] after drop: frozen={conn.IsRouteFrozen}, overrides={conn.BendRadiusOverrides.Count}, " +
                         $"blockedFlag={conn.IsBlockedFallback}, routePiercesBlocker={pierces}");
            Capture(window, canvas, Path.Combine(outputDir, "belly-2-component-on-arc.png"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Scenario (b): nested parallel connections on the right side of two stacked couplers;
    /// the inner one is styled Cobra, the outer stays Auto. Records whether the Auto sibling
    /// crosses the Cobra.
    /// </summary>
    private static async Task CaptureCobraSiblingScenario(string outputDir, List<string> findings)
    {
        var canvas = new DesignCanvasViewModel();
        var top = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("top");
        top.PhysicalX = 60;
        top.PhysicalY = 60;
        var bottom = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("bottom");
        bottom.PhysicalX = 60;
        bottom.PhysicalY = 460;
        canvas.AddComponent(top);
        canvas.AddComponent(bottom);

        var (window, _) = ShowWindow(canvas);
        try
        {
            var inner = await canvas.ConnectPinsAsync(Pin(top, "east1"), Pin(bottom, "east0"));
            var outer = await canvas.ConnectPinsAsync(Pin(top, "east0"), Pin(bottom, "east1"));
            inner.ShouldNotBeNull();
            outer.ShouldNotBeNull();
            await canvas.RecalculateRoutesAsync();
            Capture(window, canvas, Path.Combine(outputDir, "cobra-1-both-auto.png"));

            // Mirror ConnectionRoutingViewModel.OnSelectedStyleChanged for a style pick.
            inner!.Connection.Type = WaveguideType.Cobra;
            inner.Connection.InvalidateRoute();
            await canvas.RecalculateRoutesAsync();

            bool crosses = PathIntersectionDetector.Crosses(
                inner.Connection.RoutedPath!, outer!.Connection.RoutedPath!);
            findings.Add($"[cobra] after styling: innerType={inner.Connection.Type}, " +
                         $"outerBlockedFlag={outer.Connection.IsBlockedFallback}, autoCrossesCobra={crosses}");
            Capture(window, canvas, Path.Combine(outputDir, "cobra-2-auto-sibling.png"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Scenario (c): the same nested parallel pair on FLAT couplers (Cornerstone SiN footprint,
    /// 1.436 µm pin pitch — sub-cell, so grid obstacles cannot separate the fan-outs) under a
    /// 10 µm process floor. The inner connection is styled Cobra, the outer stays Auto.
    /// </summary>
    private static async Task CaptureFlatCobraSiblingScenario(string outputDir, List<string> findings)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Router.ProcessMinBendRadiusMicrometers = 30.0;
        var top = TestComponentFactory.CreateFlatCouplerWithPhysicalPins("top");
        top.PhysicalX = 60;
        top.PhysicalY = 60;
        var bottom = TestComponentFactory.CreateFlatCouplerWithPhysicalPins("bottom");
        bottom.PhysicalX = 60;
        bottom.PhysicalY = top.PhysicalY + top.HeightMicrometers + 80;
        canvas.AddComponent(top);
        canvas.AddComponent(bottom);

        var (window, _) = ShowWindow(canvas);
        double floor = canvas.Router.ProcessMinBendRadiusMicrometers;
        canvas.Routing.GetProcessMinBendRadiusMicrometers = () => floor;
        try
        {
            var inner = await canvas.ConnectPinsAsync(Pin(top, "east1"), Pin(bottom, "east0"));
            var outer = await canvas.ConnectPinsAsync(Pin(top, "east0"), Pin(bottom, "east1"));
            inner.ShouldNotBeNull();
            outer.ShouldNotBeNull();
            await canvas.RecalculateRoutesAsync();
            SetZoom(window, 2.2);
            Capture(window, canvas, Path.Combine(outputDir, "flat-cobra-1-both-auto.png"));

            // The OUTER pair takes the Cobra: its slim bulge fences in the inner pins, and the
            // 30 µm process floor makes the inner Auto U too wide to stay inside it.
            outer!.Connection.Type = WaveguideType.Cobra;
            outer.Connection.InvalidateRoute();
            await canvas.RecalculateRoutesAsync();

            bool crosses = PathIntersectionDetector.Crosses(
                inner!.Connection.RoutedPath!, outer.Connection.RoutedPath!);
            findings.Add($"[flat-cobra] after styling: outerType={outer.Connection.Type}, " +
                         $"innerBlockedFlag={inner.Connection.IsBlockedFallback}, autoCrossesCobra={crosses}");
            Capture(window, canvas, Path.Combine(outputDir, "flat-cobra-2-auto-sibling.png"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Magnifies the design canvas so micrometer-flat components stay visible.</summary>
    private static void SetZoom(Window window, double zoom)
    {
        foreach (var designCanvas in window.GetVisualDescendants().OfType<DesignCanvas>())
        {
            designCanvas.Zoom = zoom;
            designCanvas.InvalidateVisual();
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Midpoint of the (edited) arc with the given bend index along the path.</summary>
    private static (double X, double Y) ArcBellyOf(WaveguideConnection conn, int bendIndex)
    {
        int seen = -1;
        foreach (var segment in conn.GetPathSegments())
        {
            if (segment is not BendSegment bend || ++seen != bendIndex)
                continue;
            double sign = Math.Sign(bend.SweepAngleDegrees) == 0 ? 1 : Math.Sign(bend.SweepAngleDegrees);
            double midRad = (bend.StartAngleDegrees + bend.SweepAngleDegrees / 2) * Math.PI / 180;
            return (bend.Center.X + bend.RadiusMicrometers * Math.Cos(midRad - Math.PI / 2 * sign),
                    bend.Center.Y + bend.RadiusMicrometers * Math.Sin(midRad - Math.PI / 2 * sign));
        }
        throw new InvalidOperationException($"Bend #{bendIndex} not found.");
    }

    /// <summary>A pinless square component centered on the given point (the dropped obstacle).</summary>
    private static Component CreateBlockerComponent(double centerX, double centerY, double sizeMicrometers)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            new Dictionary<int, SMatrix>(), new List<CAP_Core.Components.Core.Slider>(), "blocker", "",
            parts, 0, "Blocker", DiscreteRotation.R0)
        {
            WidthMicrometers = sizeMicrometers,
            HeightMicrometers = sizeMicrometers,
            PhysicalX = centerX - sizeMicrometers / 2,
            PhysicalY = centerY - sizeMicrometers / 2,
        };
    }

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);

    private static (Window Window, MainView View) ShowWindow(DesignCanvasViewModel canvas)
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        var view = new MainView { DataContext = vm };
        var window = new Window
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>Pumps the dispatcher, forces a repaint and captures the window to a PNG.</summary>
    private static void Capture(Window window, DesignCanvasViewModel canvas, string path)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var designCanvas in window.GetVisualDescendants().OfType<DesignCanvas>())
            designCanvas.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();

        WriteableBitmap? bitmap = null;
        for (int attempt = 0; attempt < CaptureAttempts; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            if (frame == null)
                continue;
            bitmap?.Dispose();
            bitmap = frame;
        }

        bitmap.ShouldNotBeNull($"CaptureRenderedFrame stayed null after {CaptureAttempts} attempts for {path}");
        using (bitmap)
        {
            ScreenshotArtifacts.SavePng(bitmap, path);
        }
    }
}
