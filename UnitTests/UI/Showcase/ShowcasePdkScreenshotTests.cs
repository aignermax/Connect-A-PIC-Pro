using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using UnitTests.UI.Flows;
using Xunit;

namespace UnitTests.UI.Showcase;

/// <summary>
/// v0.12 feature-showcase: PDK management. Boots the real MainWindow through the UI-flow
/// host (temp user-pdk root, English pinned), stages a forked user copy that shadows its
/// bundled original plus an own user library, hovers a forked library row so the ✏/✕ quick
/// actions show, and composes library + PDK-Mgmt sections with the per-component trash
/// panel into one PNG. Opt-in via <c>UI_SHOT_DIR</c>.
/// </summary>
[Trait("Category", "Showcase")]
[Collection("UiFlows")]
public class ShowcasePdkScreenshotTests
{
    [AvaloniaFact]
    public void CapturePdkManagement()
    {
        if (!ShowcaseCapture.Enabled) return;
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var window = host.Window;
        window.Width = 1400;
        window.Height = 1150;
        Dispatcher.UIThread.RunJobs();

        StagePdkRows(vm);
        HoverForkedLibraryRow(vm, window);
        using var frame = ShowcaseCapture.CaptureFrame(window, "pdk-management.png");

        var libraryPane = SectionCrop(window, ViewportOf(window,
            UiInput.Descendants<ListBox>(window)
                .First(l => ReferenceEquals(l.ItemsSource, vm.LeftPanel.FilteredTemplates))), 74);
        var pdkPane = SectionCrop(window, ViewportOf(window,
            UiInput.Descendants<ItemsControl>(window)
                .First(c => ReferenceEquals(c.ItemsSource, vm.LeftPanel.PdkManager.LoadedPdks))), 42);
        using var trashFrame = CaptureTrashPane(host);

        ShowcaseCapture.ComposeSideBySide(
            Path.Combine(ShowcaseCapture.OutputDirectory(), "pdk-management.png"),
            new[]
            {
                ((Bitmap)frame, libraryPane),
                (frame, pdkPane),
                (trashFrame, new PixelRect(0, 0, trashFrame.PixelSize.Width, trashFrame.PixelSize.Height)),
            });
    }

    /// <summary>
    /// Registers a user library and replaces the bundled "Demo PDK" row with a forked user
    /// copy that shadows the bundled original (the state fork-on-save produces), moved to
    /// the top of the list so both rows sit inside the section's viewport.
    /// </summary>
    private static void StagePdkRows(CAP.Avalonia.ViewModels.MainViewModel vm)
    {
        var manager = vm.LeftPanel.PdkManager;
        var bundledDemo = manager.LoadedPdks.Single(p => p.Name == ShowcaseCircuit.PdkName);
        manager.LoadedPdks.Remove(bundledDemo);
        manager.RegisterPdk(ShowcaseCircuit.PdkName,
            Path.Combine(Path.GetTempPath(), "user-pdks", "demo-pdk.json"),
            isBundled: false, componentCount: bundledDemo.ComponentCount);
        manager.RegisterPdk("My SiN Library",
            Path.Combine(Path.GetTempPath(), "user-pdks", "my-sin-library.json"),
            isBundled: false, componentCount: 4);

        var fork = manager.LoadedPdks.Single(p => p.Name == ShowcaseCircuit.PdkName && !p.IsBundled);
        fork.ShadowsBundledPdk = true;
        manager.LoadedPdks.Move(manager.LoadedPdks.IndexOf(fork), 0);
        var userLib = manager.LoadedPdks.Single(p => p.Name == "My SiN Library");
        manager.LoadedPdks.Move(manager.LoadedPdks.IndexOf(userLib), 1);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Marks the forked MMI as user-editable and hovers its row (✏/✕ are hover-revealed).</summary>
    private static void HoverForkedLibraryRow(
        CAP.Avalonia.ViewModels.MainViewModel vm, CAP.Avalonia.Views.MainWindow window)
    {
        var template = vm.LeftPanel.AllTemplates
            .First(t => t.PdkSource == ShowcaseCircuit.PdkName && t.Name == "1x2 MMI Splitter");
        template.IsCustom = true;
        template.IsDeletable = true;

        var library = UiInput.Descendants<ListBox>(window)
            .First(l => ReferenceEquals(l.ItemsSource, vm.LeftPanel.FilteredTemplates));
        library.ScrollIntoView(template);
        UiInput.RunJobs();
        var row = library.ContainerFromItem(template);
        row.ShouldNotBeNull("the forked library row must be realized to hover it");
        window.MouseMove(UiInput.PointIn(window, (Visual)row!));
        UiInput.RunJobs();
    }

    /// <summary>The per-component trash flyout, staged like a real session (a deleted PDK
    /// next to an individually removed component) and rendered in its own pane.</summary>
    private static WriteableBitmap CaptureTrashPane(UiFlowTestHost host)
    {
        var store = host.UserPdkStore;
        PdkComponentDraft Comp(string name) => new()
        {
            Name = name, WidthMicrometers = 5, HeightMicrometers = 1,
            RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()",
            RawCodeBackend = "gdsfactory",
            Pins = new() { new() { Name = "o1" }, new() { Name = "o2" } },
        };
        var process = new ProcessDefinition { Name = "Demo SOI 220nm" };
        var libA = store.SaveToNamedPdk("My SiN Library", process, Comp("Ring Resonator"), "gdsfactory", null);
        store.MoveToTrash(libA);
        var libB = store.SaveToNamedPdk("Prototype Kit", process, Comp("Test MMI"), "gdsfactory", null);
        store.SaveToNamedPdk("Prototype Kit", process, Comp("Spiral Delay"), "gdsfactory", null);
        store.RemoveComponent(libB, "Test MMI");

        var vm = new CAP.Avalonia.ViewModels.Panels.PdkTrash.PdkTrashViewModel(store.CreateTrashService());
        vm.Refresh();
        var window = new Window
        {
            Width = 400, Height = 460,
            Content = new CAP.Avalonia.Views.Panels.PdkTrashPanel { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = ShowcaseCapture.CaptureFrame(window, "pdk trash pane");
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return frame;
    }

    /// <summary>The section ScrollViewer's viewport (NOT the inner ItemsControl, whose bounds
    /// grow with content) — safe window-space crops even for scrolled sections.</summary>
    private static ScrollViewer ViewportOf(Window window, Visual itemsHost) =>
        itemsHost.FindAncestorOfType<ScrollViewer>()
        ?? throw new InvalidOperationException("section has no ScrollViewer ancestor");

    /// <summary>Window-space crop of a left-panel section: the viewport plus its header above.</summary>
    private static PixelRect SectionCrop(Window window, Visual viewport, int headerHeight)
    {
        var bounds = ShowcaseCapture.BoundsIn(window, viewport);
        int top = Math.Max(0, bounds.Y - headerHeight);
        return new PixelRect(0, top, bounds.Right + 10, bounds.Bottom - top + 6);
    }
}
