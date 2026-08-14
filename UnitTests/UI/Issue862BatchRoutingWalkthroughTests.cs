using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Selection;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.Views;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Components.Connections;
using Shouldly;
using UnitTests.Helpers;
using Xunit;
using AvaloniaGrid = Avalonia.Controls.Grid;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #862 (multi-select batch routing-style change): renders the
/// real design canvas plus the real <see cref="ConnectionRoutingPanel"/> while three Auto
/// connections are box-selected, restyled to S-bend in ONE undo step, restored by a single
/// undo, and finally a plain click on one batch member keeps exactly that member selected
/// (PR #870 review finding 2). Writes step-ordered PNGs and a <c>manifest.json</c> into
/// <c>artifacts/ui-screenshots/issue-862/</c>. Opt-in via <c>UI_SHOT_DIR</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
// Heavy Skia frame captures — CI covers it, local default runs exclude Category=Slow.
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class Issue862BatchRoutingWalkthroughTests
{
    private const int WindowWidth = 1100;
    private const int WindowHeight = 980;
    private const int PanelColumnWidth = 240;
    private const int CaptureAttempts = 3;
    private const int ConnectionPairCount = 3;
    private const double PairVerticalPitch = 280;
    private const double StartComponentX = 40;
    private const double EndComponentX = 490;
    private const double EndComponentYOffset = 30;

    /// <summary>Captures the five walkthrough states and writes the caption manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue862BatchRoutingWalkthrough()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var canvas = new DesignCanvasViewModel();
        var connections = await AddConnectionPairsAsync(canvas);
        var commandManager = new CommandManager();
        var vm = MainViewModelTestHelper.CreateMainViewModel(
            canvas: canvas, commandManager: commandManager);
        vm.CanvasInteraction.CurrentMode = InteractionMode.Select;

        var manifest = new List<ManifestEntry>();
        var window = BuildWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            await CaptureScenes(window, vm, canvas, commandManager, connections, dir, manifest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        ScreenshotArtifacts.WriteText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        manifest.Count.ShouldBe(5);
    }

    /// <summary>Walks the batch-select → restyle → undo → click-member story.</summary>
    private static async Task CaptureScenes(
        Window window, MainViewModel vm, DesignCanvasViewModel canvas,
        CommandManager commandManager, List<WaveguideConnectionViewModel> connections,
        string dir, List<ManifestEntry> manifest)
    {
        RefreshCanvas(window);
        Capture(window, dir, "01-three-auto-connections.png",
            "Three independent Auto connections; the routing panel stays hidden while "
            + "nothing is selected.", manifest);

        BoxSelectAll(canvas, connections);
        connections.ShouldAllBe(c => c.IsSelected);
        RefreshCanvas(window);
        Capture(window, dir, "02-box-selected-batch.png",
            "A rubber-band drag across the canvas puts all three connections into the batch "
            + "and the routing panel announces how many it applies to.", manifest);

        vm.BottomPanel.ConnectionRouting.SelectedStyle = WaveguideType.SBend;
        await canvas.RecalculateRoutesAsync();
        connections.ShouldAllBe(c => c.Connection.Type == WaveguideType.SBend);
        commandManager.UndoCount.ShouldBe(1);
        RefreshCanvas(window);
        Capture(window, dir, "03-batch-restyled-sbend.png",
            "Picking S-bend once restyles every selected connection as a single undoable "
            + "command.", manifest);

        commandManager.Undo().ShouldBeTrue();
        await canvas.RecalculateRoutesAsync();
        connections.ShouldAllBe(c => c.Connection.Type == WaveguideType.Auto);
        RefreshCanvas(window);
        Capture(window, dir, "04-single-undo-restores-all.png",
            "One Ctrl+Z restores all three connections to their previous Auto routes.",
            manifest);

        BoxSelectAll(canvas, connections);
        var clicked = connections[0];
        var clickPoint = PathPointOutsideComponents(canvas, clicked);
        vm.CanvasInteraction.CanvasClicked(clickPoint.x, clickPoint.y);
        clicked.IsSelected.ShouldBeTrue("review finding 2: a clicked batch member stays selected");
        canvas.Selection.SelectedConnections.ShouldBeEmpty();
        RefreshCanvas(window);
        Capture(window, dir, "05-click-member-keeps-it-selected.png",
            "Clicking one batch member dissolves the batch but keeps exactly that connection "
            + "selected (review fix).", manifest);
    }

    /// <summary>Runs the production component + connection box-selection passes.</summary>
    private static void BoxSelectAll(
        DesignCanvasViewModel canvas, List<WaveguideConnectionViewModel> connections)
    {
        canvas.Selection.ClearSelection();
        ConnectionBoxSelector.SelectInRectangle(
            canvas.Selection, canvas.Connections,
            0, 0, WindowWidth, ConnectionPairCount * PairVerticalPitch + PairVerticalPitch);
        canvas.Selection.SelectedConnections.Count.ShouldBe(connections.Count);
    }

    /// <summary>Finds a routed-path point of the connection that lies outside every component.</summary>
    private static (double x, double y) PathPointOutsideComponents(
        DesignCanvasViewModel canvas, WaveguideConnectionViewModel conn)
    {
        return conn.Connection.RoutedPath!.Segments
            .Select(s => (x: (s.StartPoint.X + s.EndPoint.X) / 2, y: (s.StartPoint.Y + s.EndPoint.Y) / 2))
            .First(p => !canvas.Components.Any(c =>
                p.x >= c.X && p.x <= c.X + c.Width && p.y >= c.Y && p.y <= c.Y + c.Height));
    }

    /// <summary>Builds three vertically stacked, already-routed connection pairs.</summary>
    private static async Task<List<WaveguideConnectionViewModel>> AddConnectionPairsAsync(
        DesignCanvasViewModel canvas)
    {
        var connections = new List<WaveguideConnectionViewModel>();
        for (int i = 0; i < ConnectionPairCount; i++)
        {
            var start = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            start.PhysicalX = StartComponentX;
            start.PhysicalY = i * PairVerticalPitch;
            var end = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            end.PhysicalX = EndComponentX;
            end.PhysicalY = i * PairVerticalPitch + EndComponentYOffset;
            canvas.AddComponent(start);
            canvas.AddComponent(end);
            var connVm = await canvas.ConnectPinsAsync(
                start.PhysicalPins.First(p => p.Name == "out"),
                end.PhysicalPins.First(p => p.Name == "in"));
            connVm.ShouldNotBeNull();
            connections.Add(connVm!);
        }
        return connections;
    }

    /// <summary>Canvas on the left, the real routing panel in a fixed right column.</summary>
    private static Window BuildWindow(MainViewModel vm)
    {
        var grid = new AvaloniaGrid
        {
            ColumnDefinitions = new ColumnDefinitions($"*,{PanelColumnWidth}"),
        };
        var mainView = new MainView { DataContext = vm };
        AvaloniaGrid.SetColumn(mainView, 0);
        grid.Children.Add(mainView);
        var panel = new ConnectionRoutingPanel { DataContext = vm };
        AvaloniaGrid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Content = grid,
        };
    }

    /// <summary>Drains pending repaint jobs and forces a fresh render of the routes.</summary>
    private static void RefreshCanvas(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var designCanvas in window.GetVisualDescendants().OfType<DesignCanvas>())
            designCanvas.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Captures the shown window to a PNG (with retry) and records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
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
        bitmap.ShouldNotBeNull(
            $"CaptureRenderedFrame stayed null after {CaptureAttempts} attempts for {filename}");
        using (bitmap)
        {
            ScreenshotArtifacts.SavePng(bitmap, Path.Combine(dir, filename));
        }
        manifest.Add(new ManifestEntry(filename, caption));
    }

    /// <summary>Resolves the walkthrough output directory (repo root's artifacts folder).</summary>
    private static string ResolveOutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-862");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-862");
    }

    /// <summary>One manifest row: PNG filename plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
