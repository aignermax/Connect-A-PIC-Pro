using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.Views;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.Process;
using CAP_Core.Tiles;

namespace UnitTests.UI.Showcase;

/// <summary>
/// Stages the showcase chip — a Mach-Zehnder interferometer plus a DBR-filter and a
/// directional-coupler test structure, built entirely from real bundled Demo-PDK components
/// and wired through the production router — and boots the real MainWindow around it.
/// </summary>
internal static class ShowcaseCircuit
{
    public const string PdkName = "Demo PDK";

    /// <summary>World-space (µm) bounding box of the staged chip, for view fitting.</summary>
    private static readonly (double X, double Y, double W, double H) Extent = (20, 110, 1450, 500);

    /// <summary>
    /// Places the showcase chip onto <paramref name="vm"/>'s canvas and routes everything.
    /// Returns the MZI's bottom compensating arm — the waveguide-editing motif's subject.
    /// </summary>
    public static async Task<WaveguideConnectionViewModel> BuildChipAsync(MainViewModel vm)
    {
        var canvas = vm.Canvas;

        // Row 1: the Mach-Zehnder interferometer.
        var gcIn = Place(vm, "Grating Coupler", 30, 290);
        var splitter = Place(vm, "1x2 MMI Splitter", 200, 272);
        var shifter = Place(vm, "Phase Shifter", 380, 130);
        var combiner = Place(vm, "2x2 MMI Coupler", 1000, 270);
        var detector = Place(vm, "Photodetector", 1330, 150);
        var gcOut = Place(vm, "Grating Coupler", 1360, 370, DiscreteRotation.R180);
        await Connect(canvas, gcIn, "waveguide", splitter, "in");
        await Connect(canvas, splitter, "out1", shifter, "in");
        await Connect(canvas, shifter, "out", combiner, "in1");
        var bottomArm = await Connect(canvas, splitter, "out2", combiner, "in2");
        await Connect(canvas, combiner, "out1", detector, "in");
        await Connect(canvas, combiner, "out2", gcOut, "waveguide");

        // Row 2: DBR-filter and directional-coupler test structures.
        var gcB1 = Place(vm, "Grating Coupler", 30, 520);
        var dbr = Place(vm, "DBR Filter", 210, 380);
        var gcB2 = Place(vm, "Grating Coupler", 390, 541, DiscreteRotation.R180);
        await Connect(canvas, gcB1, "waveguide", dbr, "in");
        await Connect(canvas, dbr, "out", gcB2, "waveguide");

        var gcC1 = Place(vm, "Grating Coupler", 600, 520);
        var coupler = Place(vm, "Directional Coupler", 770, 490);
        var gcC2 = Place(vm, "Grating Coupler", 1120, 470, DiscreteRotation.R180);
        var gcC3 = Place(vm, "Grating Coupler", 1120, 545, DiscreteRotation.R180);
        await Connect(canvas, gcC1, "waveguide", coupler, "in1");
        await Connect(canvas, coupler, "out1", gcC2, "waveguide");
        await Connect(canvas, coupler, "out2", gcC3, "waveguide");

        await WaitForRoutingIdleAsync(canvas);
        return bottomArm;
    }

    /// <summary>
    /// Boots the real MainWindow (DataContext after Show, so Loaded's DI wiring no-ops)
    /// around the staged chip with an isolated group library, an active Playground process
    /// and a fitted view. Caller closes the window.
    /// </summary>
    public static async Task<(MainViewModel Vm, MainWindow Window, WaveguideConnectionViewModel BottomArm)>
        BootStagedMainWindowAsync()
    {
        var groupLibrary = new CAP_Core.Components.Creation.GroupLibraryManager(
            Path.Combine(Path.GetTempPath(), $"lunima-showcase-groups-{Guid.NewGuid():N}"));
        var vm = UnitTests.Helpers.MainViewModelTestHelper.CreateMainViewModel(libraryManager: groupLibrary);
        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.Playground(), markDirty: false);
        var bottomArm = await BuildChipAsync(vm);

        var window = new MainWindow { Width = 1480, Height = 950 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.DataContext = vm;
        Dispatcher.UIThread.RunJobs();

        FitView(window, vm);
        await WaitForRoutingIdleAsync(vm.Canvas);
        vm.StatusText = "Ready";
        return (vm, window, bottomArm);
    }

    /// <summary>Fits the whole staged chip into the design canvas (zoom + pan).</summary>
    public static void FitView(MainWindow window, MainViewModel vm) =>
        SetView(window, vm, Extent);

    /// <summary>Zooms onto a world-space region (µm) — for close-up motifs.</summary>
    public static void SetView(
        MainWindow window, MainViewModel vm, (double X, double Y, double W, double H) region)
    {
        var canvasControl = window.GetVisualDescendants()
            .OfType<CAP.Avalonia.Controls.DesignCanvas>().First();
        const double margin = 60;
        double zoom = Math.Min(
            (canvasControl.Bounds.Width - margin) / region.W,
            (canvasControl.Bounds.Height - margin) / region.H);
        canvasControl.Zoom = Math.Clamp(zoom, 0.1, 1.5);

        vm.Canvas.PanX = canvasControl.Bounds.Width / 2 - (region.X + region.W / 2) * canvasControl.Zoom;
        vm.Canvas.PanY = canvasControl.Bounds.Height / 2 - (region.Y + region.H / 2) * canvasControl.Zoom;
        canvasControl.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Waits until the router settles, then restyles any connection the A* router gave up
    /// on to the SBend primitive — no showcase frame may contain a red unrouted placeholder.
    /// </summary>
    public static async Task WaitForRoutingIdleAsync(DesignCanvasViewModel canvas)
    {
        await canvas.RecalculateRoutesAsync();
        for (int i = 0; i < 100 && canvas.IsRouting; i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        var unrouted = canvas.Connections
            .Where(c => c.Connection.GetPathSegments() is not { Count: > 0 })
            .ToList();
        if (unrouted.Count == 0) return;
        foreach (var connection in unrouted)
        {
            connection.Connection.Type = WaveguideType.SBend;
            connection.Connection.InvalidateRoute();
        }
        await canvas.RecalculateRoutesAsync();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Instantiates a bundled Demo-PDK template at world position (µm), applying the
    /// requested rotation through the production <see cref="CAP.Avalonia.Commands.RotateComponentCommand"/>
    /// (which also rotates the physical-pin offsets — the raw rotation property does not).
    /// </summary>
    private static Component Place(
        MainViewModel vm, string templateName, double x, double y,
        DiscreteRotation rotation = DiscreteRotation.R0)
    {
        var template = vm.LeftPanel.AllTemplates
            .First(t => t.PdkSource == PdkName && t.Name == templateName);
        var component = ComponentTemplates.CreateFromTemplate(template, x, y);
        var componentVm = vm.Canvas.AddComponent(component, template.Name, template.PdkSource);

        for (int i = 0; i < (int)rotation; i++)
        {
            var rotate = new CAP.Avalonia.Commands.RotateComponentCommand(vm.Canvas, componentVm);
            rotate.Execute();
            if (!rotate.WasApplied)
                throw new InvalidOperationException($"rotation blocked for {templateName} at ({x}, {y})");
        }
        return component;
    }

    /// <summary>Connects two named physical pins through the production routing pipeline.</summary>
    private static async Task<WaveguideConnectionViewModel> Connect(
        DesignCanvasViewModel canvas, Component from, string fromPin, Component to, string toPin)
    {
        var start = from.PhysicalPins.First(p => p.Name == fromPin);
        var end = to.PhysicalPins.First(p => p.Name == toPin);
        var connection = await canvas.ConnectPinsAsync(start, end);
        return connection ?? throw new InvalidOperationException(
            $"routing failed for {from.Name}.{fromPin} → {to.Name}.{toPin}");
    }
}
