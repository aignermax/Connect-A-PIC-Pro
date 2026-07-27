using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// User story for issue #798 (Cut tool): arm Cut mode via the toolbar command, hover the
/// intersection of a pin guide line with a waveguide, CLICK it — a crossing component is
/// inserted, the connection splits into two halves, and undo restores the original
/// connection. Clicking empty canvas keeps the tool armed.
/// </summary>
[Trait("Category", "UiFlows")]
// Boots the real MainWindow through the input pipeline — too heavy for local default
// runs (CI covers it, the local runners exclude Category=Slow).
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class UiFlowCutToolTests
{
    private const string SiepicPdkFile = "siepic-ebeam-pdk.json";
    private const int RenderAttempts = 3;

    [AvaloniaFact]
    public void CutClick_onGuideIntersection_insertsCrossingAndUndoRestores()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;
        EnsureCrossingTemplateLoaded(vm);
        var original = BuildCutScene(vm);

        vm.CanvasInteraction.SetCutModeCommand.Execute(null);
        UiInput.RunJobs();
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.Cut);

        var canvasControl = UiInput.Descendants<DesignCanvas>(win).First();
        ForceRenderPasses(canvasControl);
        canvasControl.InteractionState.CutCandidates.ShouldNotBeEmpty(
            $"Cut mode must surface the guide/waveguide intersection (status: {vm.StatusText})");

        // Click the candidate at world (200, 100) through the real input pipeline.
        UiInput.ClickAt(win, CanvasPoint(win, canvasControl, 200, 100));

        var crossings = vm.Canvas.Components.Where(
            c => c.Component.NazcaFunctionName == CrossingComponentInstance.CrossingNazcaFunctionName).ToList();
        crossings.Count.ShouldBe(1,
            $"the click must insert the PDK crossing (status: {vm.StatusText})");
        var crossingVm = crossings[0];
        vm.Canvas.ConnectionManager.Connections.Count.ShouldBe(2,
            "the original connection must split into two halves docked at the crossing");
        vm.Canvas.ConnectionManager.Connections.ShouldNotContain(original);
        crossingVm.Component.IsInsertedCrossing.ShouldBeFalse(
            "a manual crossing is user intent — the adaptive pass must never dissolve it");
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.Cut,
            "the tool stays armed for further cuts");

        vm.CommandManager.Undo().ShouldBeTrue();
        UiInput.RunJobs();
        vm.Canvas.Components.Count(
                c => c.Component.NazcaFunctionName == CrossingComponentInstance.CrossingNazcaFunctionName)
            .ShouldBe(0, "undo removes the crossing again");
        vm.Canvas.ConnectionManager.Connections.ShouldHaveSingleItem().ShouldBeSameAs(original,
            "undo restores the original connection object (fine-tuning preserved)");
    }

    [AvaloniaFact]
    public void CutClick_onEmptyCanvas_keepsTheToolArmed()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;
        EnsureCrossingTemplateLoaded(vm);
        BuildCutScene(vm);

        vm.CanvasInteraction.SetCutModeCommand.Execute(null);
        UiInput.RunJobs();

        var canvasControl = UiInput.Descendants<DesignCanvas>(win).First();
        ForceRenderPasses(canvasControl);
        UiInput.ClickAt(win, CanvasPoint(win, canvasControl, 600, 300));

        vm.Canvas.Components.ShouldNotContain(
            c => c.Component.NazcaFunctionName == CrossingComponentInstance.CrossingNazcaFunctionName,
            "an empty click inserts nothing");
        vm.Canvas.ConnectionManager.Connections.Count.ShouldBe(1);
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.Cut,
            "a miss keeps the Cut tool armed");
    }

    /// <summary>
    /// Horizontal waveguide (10,100)→(390,100) with a deterministic straight route, plus a
    /// guide terminal above it whose south-facing pin ray crosses the waveguide at (200, 100).
    /// </summary>
    private static CAP_Core.Components.Connections.WaveguideConnection BuildCutScene(
        CAP.Avalonia.ViewModels.MainViewModel vm)
    {
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        var guide = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        vm.Canvas.AddComponent(left.Component, "Terminal");
        vm.Canvas.AddComponent(right.Component, "Terminal");
        vm.Canvas.AddComponent(guide.Component, "Terminal");

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(10, 100, 390, 100, 0));
        var connectionVm = vm.Canvas.ConnectPinsWithCachedRoute(
            left.PhysicalPin, right.PhysicalPin, route);
        UiInput.RunJobs();
        return connectionVm!.Connection;
    }

    /// <summary>Loads the bundled SiEPIC crossing template unless the panel already has one.</summary>
    private static void EnsureCrossingTemplateLoaded(CAP.Avalonia.ViewModels.MainViewModel vm)
    {
        if (CrossingComponentInstance.FindCrossingTemplate(vm.LeftPanel.AllTemplates) != null) return;
        var templates = TestPdkLoader.LoadFromPdk(SiepicPdkFile);
        vm.LeftPanel.AllTemplates.Add(templates.Single(t => string.Equals(
            t.NazcaFunctionName, CrossingComponentInstance.CrossingNazcaFunctionName,
            StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Pumps headless render ticks so the Cut overlay's per-frame candidate computation runs
    /// before the test clicks (the compositor renders on the forced timer, not on RunJobs).
    /// </summary>
    private static void ForceRenderPasses(DesignCanvas canvasControl)
    {
        for (int i = 0; i < RenderAttempts && canvasControl.InteractionState.CutCandidates.Count == 0; i++)
        {
            canvasControl.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            UiInput.RunJobs();
        }
    }

    private static Point CanvasPoint(
        global::Avalonia.Controls.Window win, DesignCanvas canvasControl, double x, double y)
    {
        var vm = (CAP.Avalonia.ViewModels.MainViewModel)win.DataContext!;
        return canvasControl.TranslatePoint(
            new Point(x * canvasControl.Zoom + vm.Canvas.PanX, y * canvasControl.Zoom + vm.Canvas.PanY),
            win)!.Value;
    }
}
