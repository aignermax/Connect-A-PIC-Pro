using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Routing.CrossingInsertion;
using Xunit;
using AvaloniaFactAttribute = Avalonia.Headless.XUnit.AvaloniaFactAttribute;
using AvaloniaCanvas = Avalonia.Controls.Canvas;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #705 / PR #718 (crossing insertion lifecycle): drives the
/// production canvas view-model through the UI delete/undo commands this PR fixes and
/// renders the resulting canvas state as step-ordered headless PNGs into
/// <c>artifacts/ui-screenshots/issue-705/</c> plus a <c>manifest.json</c> with captions.
/// Uses the same Skia headless harness as <see cref="UiScreenshotTests"/>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue705WalkthroughScreenshotTests
{
    // These frames are legitimately sparse: a dark canvas with a few flat-colored blocks
    // and thin route lines samples ~6 distinct colors on the 64×64 grid, versus 1 blank.
    private const int MinDistinctSampledColors = 4;
    private const int SampleGridSize = 64;
    private const double CanvasWidthPixels = 1000;

    /// <summary>Bend loss that makes a detour clearly worse than one crossing.</summary>
    private const double ExpensiveBendLossDb = 0.5;

    /// <summary>Fixed world viewport so all frames of the cross layout are comparable.</summary>
    private static readonly Rect Viewport = new(-25, 15, 450, 370);

    private sealed record Fixture(
        DesignCanvasViewModel Canvas, CrossingInsertionCanvasBinder Binder,
        CrossingTestCircuit.Terminal ALeft, CrossingTestCircuit.Terminal ARight,
        CrossingTestCircuit.Terminal BTop, CrossingTestCircuit.Terminal BBottom);

    /// <summary>Renders the six walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue705Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        await CaptureConnectionDeleteUndoSteps(dir, manifest);
        await CaptureComponentDeleteUndoSteps(dir, manifest);
        CaptureInsertionRollbackStep(dir, manifest);

        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        manifest.Count.ShouldBe(6);
    }

    /// <summary>Steps 1–3: deleting a crossing sub-connection dissolves; undo re-inserts.</summary>
    private async Task CaptureConnectionDeleteUndoSteps(string dir, List<ManifestEntry> manifest)
    {
        var fixture = BuildCanvas();
        var record = fixture.Binder.Service.Records.ShouldHaveSingleItem();
        CaptureCanvas(fixture, dir, "01-crossing-inserted.png",
            "Two nets intersect, so the routing pass automatically splits them through an "
            + "inserted crossing component (gold) at the intersection.", manifest);

        var subVm = fixture.Canvas.Connections.First(
            vm => vm.Connection == record.SubConnectionsB[0]);
        var command = new DeleteConnectionCommand(fixture.Canvas, subVm);
        command.Execute();
        await fixture.Canvas.RecalculateRoutesAsync();
        CaptureCanvas(fixture, dir, "02-delete-sub-dissolves-crossing.png",
            "Deleting one sub-connection of the vertical net dissolves the whole crossing and "
            + "restores the untouched horizontal net unsplit instead of orphaning the component.",
            manifest);

        command.Undo();
        await fixture.Canvas.RecalculateRoutesAsync();
        fixture.Binder.Service.Records.Count.ShouldBe(1);
        CaptureCanvas(fixture, dir, "03-undo-reinserts-crossing.png",
            "Undoing the delete restores the vertical net and the routing pass re-evaluates "
            + "and re-inserts the crossing without ghost pins or duplicate connections.", manifest);
    }

    /// <summary>Steps 4–5: deleting a net endpoint component dissolves; undo restores both nets.</summary>
    private async Task CaptureComponentDeleteUndoSteps(string dir, List<ManifestEntry> manifest)
    {
        var fixture = BuildCanvas();
        var record = fixture.Binder.Service.Records.ShouldHaveSingleItem();
        var terminalVm = fixture.Canvas.Components.First(
            vm => vm.Component == fixture.ARight.Component);
        var command = new DeleteComponentCommand(fixture.Canvas, terminalVm);

        command.Execute();
        await fixture.Canvas.RecalculateRoutesAsync();
        fixture.Canvas.Components.ShouldNotContain(vm => vm.Component == record.CrossingComponent);
        CaptureCanvas(fixture, dir, "04-delete-endpoint-dissolves-crossing.png",
            "Deleting the A_right terminal removes the horizontal net, dissolves the crossing, "
            + "and restores the vertical net unsplit.", manifest);

        command.Undo();
        await fixture.Canvas.RecalculateRoutesAsync();
        fixture.Binder.Service.Records.Count.ShouldBe(1);
        CaptureCanvas(fixture, dir, "05-undo-restores-and-reinserts.png",
            "Undoing the component delete restores both nets, which are re-evaluated and split "
            + "through a fresh crossing again.", manifest);
    }

    /// <summary>Step 6: an unroutable crossing port rolls the insertion back, keeping the detour.</summary>
    private void CaptureInsertionRollbackStep(string dir, List<ManifestEntry> manifest)
    {
        var wall = CrossingTestCircuit.CreateTerminal("wall", 300, 200, 0);
        wall.Component.WidthMicrometers = 100;
        wall.Component.HeightMicrometers = 100;
        var layout = CrossingTestCircuit.Build(
            ExpensiveBendLossDb, CreateCrossingWithBuriedPort,
            extraComponents: new[] { wall.Component });
        layout.AddedCrossings.ShouldBeEmpty();

        var components = new List<Component>
        {
            layout.ALeft.Component, layout.ARight.Component,
            layout.BTop.Component, layout.BBottom.Component, wall.Component,
        };
        Capture(Issue705CrossingSceneRenderer.Render(
                components, layout.Manager.Connections.ToList(), CanvasWidthPixels),
            dir, "06-unroutable-insertion-rolls-back.png",
            "When a crossing port cannot be routed (sabotaged port buried in the wall) the "
            + "insertion rolls back completely and the working detour route is kept.", manifest);
    }

    /// <summary>Canvas with crossing insertion enabled, four terminals, and both nets wired.</summary>
    private static Fixture BuildCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting(0, 0, 400, 400);
        canvas.ConnectionManager.DefaultBendLossDbPer90Deg = ExpensiveBendLossDb;

        var binder = new CrossingInsertionCanvasBinder(
            canvas,
            () => new CrossingComponentInstance(
                CrossingTestCircuit.CreateCrossingComponent(), "Crossing 4-Port", "SiEPIC EBeam"),
            uiDispatch: action => action())
        {
            IsEnabled = true,
        };

        var aLeft = CrossingTestCircuit.CreateTerminal("A_left", 0, 95, 0, sourceCoupling: 1.0);
        var aRight = CrossingTestCircuit.CreateTerminal("A_right", 390, 95, 180);
        var bTop = CrossingTestCircuit.CreateTerminal("B_top", 195, 40, 90);
        var bBottom = CrossingTestCircuit.CreateTerminal("B_bottom", 195, 350, 270);
        foreach (var terminal in new[] { aLeft, aRight, bTop, bBottom })
            canvas.AddComponent(terminal.Component);

        canvas.ConnectPins(bTop.PhysicalPin, bBottom.PhysicalPin);
        canvas.ConnectPins(aLeft.PhysicalPin, aRight.PhysicalPin);
        canvas.ConnectionManager.RecalculateAllTransmissions();

        return new Fixture(canvas, binder, aLeft, aRight, bTop, bBottom);
    }

    /// <summary>Renders the fixture's current canvas state (components + routed nets).</summary>
    private static void CaptureCanvas(
        Fixture fixture, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        var components = fixture.Canvas.Components.Select(vm => vm.Component).ToList();
        var connections = fixture.Canvas.ConnectionManager.Connections.ToList();
        Capture(Issue705CrossingSceneRenderer.Render(components, connections, CanvasWidthPixels, Viewport),
            dir, filename, caption, manifest);
    }

    /// <summary>
    /// A crossing whose north port is displaced into the center of the wall at
    /// (300, 200)..(400, 300) once centered on the (200, 100) intersection, making the
    /// north sub-connection unroutable so the insertion must roll back.
    /// </summary>
    private static Component CreateCrossingWithBuriedPort()
    {
        var crossing = CrossingTestCircuit.CreateCrossingComponent();
        var north = crossing.PhysicalPins.First(p => p.AngleDegrees == 270);
        north.OffsetXMicrometers = 350.0 - (200.0 - CrossingTestCircuit.CrossingEdgeMicrometers / 2.0);
        north.OffsetYMicrometers = 250.0 - (100.0 - CrossingTestCircuit.CrossingEdgeMicrometers / 2.0);
        return crossing;
    }

    /// <summary>Shows the canvas in a headless window, captures a PNG, and records the caption.</summary>
    private static void Capture(
        AvaloniaCanvas scene, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        var window = new Window
        {
            Width = scene.Width,
            Height = scene.Height,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = scene,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();
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
        if (fb.Size.Width <= 0 || fb.Size.Height <= 0) return 0;

        int stepX = Math.Max(1, fb.Size.Width / SampleGridSize);
        int stepY = Math.Max(1, fb.Size.Height / SampleGridSize);
        var colors = new HashSet<int>();
        for (int y = 0; y < fb.Size.Height; y += stepY)
        {
            var rowAddr = fb.Address + y * fb.RowBytes;
            for (int x = 0; x < fb.Size.Width; x += stepX)
                colors.Add(Marshal.ReadInt32(rowAddr, x * 4));
        }
        return colors.Count;
    }

    /// <summary>Repo-root walkthrough output directory (env override: <c>UI_SHOT_DIR</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-705");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-705");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-705");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
