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
/// Stages the showcase chip — a Mach-Zehnder interferometer with a thermo-optic phase
/// shifter in the top arm and a DBR filter in the bottom arm, plus a DC electrical block
/// (probe pads on the modulator contacts, bond pads on the photodetector diode) wired as
/// metal traces — built entirely from real bundled PDK components and routed through the
/// production router with diagonal pathfinding enabled. Boots the real MainWindow around it.
/// </summary>
internal static class ShowcaseCircuit
{
    public const string PdkName = "Demo PDK";

    /// <summary>The SiEPIC bundled PDK, source of the purely electrical Bond Pad.</summary>
    public const string SiepicPdkName = "SiEPIC EBeam PDK";

    /// <summary>World-space (µm) bounding box of the staged chip, for view fitting.</summary>
    private static readonly (double X, double Y, double W, double H) Extent = (20, 15, 1510, 595);

    /// <summary>Canvas pixels kept above the chip origin by <see cref="FitView"/> — the
    /// wide staged chip leaves vertical slack, which should show chip area, not the void
    /// above the wafer.</summary>
    private const double ChipTopMarginPx = 40;

    /// <summary>
    /// Places the showcase chip onto <paramref name="vm"/>'s canvas and routes everything
    /// (diagonal pathfinding on, two connections manually styled). Returns the MZI's
    /// bottom-arm DBR→combiner link — the waveguide-editing motif's subject.
    /// </summary>
    public static async Task<WaveguideConnectionViewModel> BuildChipAsync(MainViewModel vm)
    {
        var canvas = vm.Canvas;
        canvas.UseDiagonalRouting = true;

        // The MZI: input coupler → 1x2 splitter → phase-shifter arm / DBR-filter arm →
        // 2x2 combiner → photodetector (bar) + output coupler (cross).
        var gcIn = Place(vm, "Grating Coupler", 30, 320);
        var splitter = Place(vm, "1x2 MMI Splitter", 260, 302);
        var shifter = Place(vm, "Phase Shifter", 420, 170);
        var strt = Place(vm, "Straight Waveguide 100µm", 500, 585);
        var dbr = Place(vm, "DBR Filter", 700, 420);
        var combiner = Place(vm, "2x2 MMI Coupler", 990, 300);
        var detector = Place(vm, "Photodetector", 1310, 170);
        var gcOut = Place(vm, "Grating Coupler", 1280, 480, DiscreteRotation.R180);

        // Only the input coupler injects light; the output coupler listens (#690), which
        // also makes it the transient analysis output.
        gcOut.LaserEnabled = false;

        await Connect(canvas, gcIn, "waveguide", splitter, "in");
        await Connect(canvas, splitter, "out1", shifter, "in");
        await Connect(canvas, shifter, "out", combiner, "in1");
        var intoDbr = await Connect(canvas, splitter, "out2", strt, "a0");
        await Connect(canvas, strt, "b0", dbr, "in");
        var bottomArm = await Connect(canvas, dbr, "out", combiner, "in2");
        await Connect(canvas, combiner, "out1", detector, "in");
        var toOutput = await Connect(canvas, combiner, "out2", gcOut, "waveguide");

        // The DC electrical block: probe pads drive the phase-shifter heater contacts,
        // bond pads contact the photodetector diode — all as thick metal traces.
        var probe1 = Place(vm, "Probe Pad", 460, 25, DiscreteRotation.R180);
        var probe2 = Place(vm, "Probe Pad", 640, 25, DiscreteRotation.R180);
        var bond1 = Place(vm, "Bond Pad", 1120, 25, DiscreteRotation.R180, SiepicPdkName);
        var bond2 = Place(vm, "Bond Pad", 1430, 25, DiscreteRotation.R180, SiepicPdkName);
        await Connect(canvas, probe1, "pad", shifter, "elec1");
        await Connect(canvas, probe2, "pad", shifter, "elec2");
        await Connect(canvas, bond1, "elec", detector, "anode");
        await Connect(canvas, bond2, "elec", detector, "cathode");

        // Two hand-styled waveguides (#755): the splitter→DBR drop as a sine S-curve and
        // the combiner→output link as a single arc with a custom 25 µm bend radius.
        await StyleConnection(canvas, intoDbr, WaveguideType.SBend);
        await StyleConnection(canvas, toOutput, WaveguideType.Bend, bendRadiusMicrometers: 25);

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

        var window = new MainWindow { Width = 1920, Height = 950 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.DataContext = vm;
        Dispatcher.UIThread.RunJobs();

        // Give the canvas the space a designer would: the properties column at a compact
        // width instead of its content-driven maximum.
        window.GetVisualDescendants().OfType<global::Avalonia.Controls.Border>()
            .First(b => b.Name == "RightPanelBorder").Width = 420;
        Dispatcher.UIThread.RunJobs();

        FitView(window, vm);
        await WaitForRoutingIdleAsync(vm.Canvas);
        vm.StatusText = "Ready";
        return (vm, window, bottomArm);
    }

    /// <summary>Fits the whole staged chip into the design canvas (zoom + pan), keeping
    /// the vertical slack below the circuit (inside the chip) instead of above the wafer.</summary>
    public static void FitView(MainWindow window, MainViewModel vm)
    {
        SetView(window, vm, Extent);
        if (vm.Canvas.PanY <= ChipTopMarginPx) return;
        vm.Canvas.PanY = ChipTopMarginPx;
        window.GetVisualDescendants()
            .OfType<CAP.Avalonia.Controls.DesignCanvas>().First().InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
    }

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
    /// Applies a manual routing style (and optional custom bend radius) to a connection —
    /// the staged equivalent of picking a style in the Routing panel (#755).
    /// </summary>
    private static async Task StyleConnection(
        DesignCanvasViewModel canvas, WaveguideConnectionViewModel connection,
        WaveguideType style, double? bendRadiusMicrometers = null)
    {
        if (bendRadiusMicrometers is { } radius)
            connection.Connection.BendRadiusMicrometers = radius;
        connection.Connection.Type = style;
        connection.Connection.InvalidateRoute();
        await canvas.RecalculateRoutesAsync();
    }

    /// <summary>
    /// Instantiates a bundled PDK template at world position (µm), applying the
    /// requested rotation through the production <see cref="CAP.Avalonia.Commands.RotateComponentCommand"/>
    /// (which also rotates the physical-pin offsets — the raw rotation property does not).
    /// </summary>
    private static Component Place(
        MainViewModel vm, string templateName, double x, double y,
        DiscreteRotation rotation = DiscreteRotation.R0, string pdkSource = PdkName)
    {
        var template = vm.LeftPanel.AllTemplates
            .First(t => t.PdkSource == pdkSource && t.Name == templateName);
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

    /// <summary>Connects two named physical pins through the production routing pipeline —
    /// optical pairs become waveguides, electrical pairs metal traces.</summary>
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
