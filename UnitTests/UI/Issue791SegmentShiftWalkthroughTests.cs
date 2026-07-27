using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Canvas.SegmentShiftHandles;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using Shouldly;
using UnitTests.Helpers;
using Xunit;
using AvaloniaFactAttribute = Avalonia.Headless.XUnit.AvaloniaFactAttribute;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #791 (parallel-shift of straight waveguide segments):
/// renders the production <see cref="SegmentShiftHandleRenderer"/> over a routed Z-path in
/// four states — idle handle, live perpendicular drag with Δ label, clamped drag (red), and
/// after undo — as step-ordered PNGs + manifest.json in
/// <c>artifacts/ui-screenshots/issue-791/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue791SegmentShiftWalkthroughTests
{
    // A dark canvas with one route and a few handles is legitimately sparse (like #705).
    private const int MinDistinctSampledColors = 4;
    private const int SampleGridSize = 64;
    private const int CanvasWidthPixels = 1000;
    private const int MiddleStraightIndex = 1;
    private const double ShiftMicrometers = 35.0;

    /// <summary>Fixed world viewport (µm) so all frames are comparable.</summary>
    private static readonly Rect Viewport = new(-30, -40, 330, 210);

    /// <summary>Captures the four walkthrough steps and writes the caption manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue791SegmentShiftWalkthrough()
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
        var connection = CreateRoutedZConnection();
        var connectionVm = new WaveguideConnectionViewModel(connection);
        vm.CanvasInteraction.SelectedWaveguideConnection = connectionVm;
        var state = new CanvasInteractionState();
        var manifest = new List<object>();

        // Step 1: selecting the connection reveals a diamond midpoint handle on the
        // shiftable straight (pin-adjacent straights get none).
        Capture(vm, state, connection, dir, "01-handle-on-selected-connection.png");
        manifest.Add(new
        {
            file = "01-handle-on-selected-connection.png",
            caption = "Selecting a connection shows a diamond midpoint handle on each shiftable "
                + "straight segment — same visual language as the bend-radius handles, but "
                + "diamond-shaped so the two edits are distinguishable.",
        });

        // Step 2: a live drag applies the shift perpendicular to the segment; the two
        // neighbour bends slide along the outer straights and the Δ µm readout follows.
        SegmentShiftEditor.TryApplyShift(connection, MiddleStraightIndex, ShiftMicrometers, out _)
            .ShouldBeTrue();
        state.ActiveShiftStraightIndex = MiddleStraightIndex;
        state.ActiveShiftDeltaMicrometers = ShiftMicrometers;
        Capture(vm, state, connection, dir, "02-live-perpendicular-drag.png");
        manifest.Add(new
        {
            file = "02-live-perpendicular-drag.png",
            caption = "Dragging moves the segment parallel to itself (only the perpendicular "
                + "pointer component counts); the adjoining bends re-fit live and the handle "
                + "shows the running Δ µm offset.",
        });

        // Step 3: a drag past the collapse limit keeps the last valid geometry and paints
        // the handle red — honest clamp instead of silent geometry corruption.
        SegmentShiftEditor.TryApplyShift(connection, MiddleStraightIndex, 500, out _)
            .ShouldBeFalse("a shift collapsing the incoming straight must be rejected");
        state.ActiveShiftClamped = true;
        Capture(vm, state, connection, dir, "03-clamped-at-collapse-limit.png");
        manifest.Add(new
        {
            file = "03-clamped-at-collapse-limit.png",
            caption = "Dragging past the point where a neighbour segment would collapse clamps "
                + "the edit: the geometry keeps the last valid shape and the handle turns red.",
        });

        // Step 4: the whole drag committed as one command — Ctrl+Z restores the original route.
        state.ActiveShiftStraightIndex = -1;
        state.ActiveShiftClamped = false;
        state.ActiveShiftDeltaMicrometers = 0;
        var command = new SegmentShiftCommand(connectionVm, MiddleStraightIndex,
                                              beforeOffset: 0, afterOffset: ShiftMicrometers);
        command.Execute(); // no-op re-apply of the live drag, mirrors the recognizer
        command.Undo();
        Capture(vm, state, connection, dir, "04-undo-restores-route.png");
        manifest.Add(new
        {
            file = "04-undo-restores-route.png",
            caption = "The whole drag is one undoable command: Ctrl+Z returns the segment and "
                + "both bends exactly to the pre-drag route.",
        });

        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Capture(MainViewModel vm, CanvasInteractionState state,
        WaveguideConnection connection, string outputDir, string filename)
    {
        var scene = new Issue791SegmentShiftSceneControl(vm, state, connection, Viewport)
        {
            Width = CanvasWidthPixels,
            Height = CanvasWidthPixels * Viewport.Height / Viewport.Width,
        };
        var window = new Window
        {
            Width = scene.Width,
            Height = scene.Height,
            Content = scene,
            Background = Brushes.Black,
        };
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

    /// <summary>Z-path with one shiftable middle straight: east 100 µm, 90° bend (r=20),
    /// north 80 µm, 90° bend back (r=20), east 120 µm — pins on both ends.</summary>
    private static WaveguideConnection CreateRoutedZConnection()
    {
        var conn = new WaveguideConnection
        {
            StartPin = new PhysicalPin { Name = "output", AngleDegrees = 0 },
            EndPin = new PhysicalPin { Name = "input", AngleDegrees = 180 },
        };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        path.Segments.Add(new BendSegment(100, 20, 20, 0, 90));
        path.Segments.Add(new StraightSegment(120, 20, 120, 100, 90));
        path.Segments.Add(new BendSegment(140, 100, 20, 90, -90));
        path.Segments.Add(new StraightSegment(140, 120, 260, 120, 0));
        conn.RestoreCachedPath(path);
        SegmentShiftGeometry.GetHandles(path.Segments).ShouldHaveSingleItem();
        return conn;
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

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-791</c>, or <c>UI_SHOT_DIR/issue-791</c>.</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-791");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-791");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-791");
    }
}
