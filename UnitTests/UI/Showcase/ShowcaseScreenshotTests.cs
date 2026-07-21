using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Components.Connections;
using Shouldly;
using UnitTests.Helpers;
using Xunit;
using AvGrid = Avalonia.Controls.Grid;

namespace UnitTests.UI.Showcase;

/// <summary>
/// v0.12 feature-showcase (canvas motifs): the hero shot of a staged photonic chip (MZI +
/// filter/coupler test structures) in the real MainWindow, the Figma-style waveguide-editing
/// motif (selected connection with bend-radius handles + routing panel) and the
/// gdsfactory-YAML netlist export view. Opt-in via <c>UI_SHOT_DIR</c>; PNGs land in
/// <c>UI_SHOT_DIR/v0.12/</c>.
/// </summary>
[Trait("Category", "Showcase")]
[Collection("LocalizationSingleton")]
public class ShowcaseScreenshotTests
{
    [AvaloniaFact]
    public async Task CaptureHeroCanvas()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            var (vm, window, _) = await ShowcaseCircuit.BootStagedMainWindowAsync();
            vm.Canvas.Connections.Count.ShouldBe(12);

            // Run the real CW S-matrix simulation (the input coupler's laser drives the
            // chip) so the hero shows the live power-flow overlay on every waveguide.
            await vm.RunSimulationCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            vm.Canvas.ShowPowerFlow.ShouldBeTrue("the CW run must enable the power-flow overlay");
            vm.Canvas.PowerFlowVisualizer.CurrentResult.ShouldNotBeNull();

            ShowcaseCapture.CaptureWindow(
                window, Path.Combine(ShowcaseCapture.OutputDirectory(), "hero-canvas.png"));
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    [AvaloniaFact]
    public async Task CaptureWaveguideEditing()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            var (vm, window, bottomArm) = await ShowcaseCircuit.BootStagedMainWindowAsync();

            // Select the DBR→combiner arm like a user clicking it: the canvas draws the
            // blue bend-radius handles and the Routing style section opens.
            vm.CanvasInteraction.SelectedWaveguideConnection = bottomArm;
            Dispatcher.UIThread.RunJobs();
            vm.BottomPanel.ConnectionRouting.SelectedStyle = WaveguideType.Bend;
            await ShowcaseCircuit.WaitForRoutingIdleAsync(vm.Canvas);
            bottomArm.Connection.Type.ShouldBe(WaveguideType.Bend);

            // Zoom onto the edited arm so the screen-constant handles read clearly.
            ShowcaseCircuit.SetView(window, vm, (610, 300, 660, 340));
            vm.StatusText = "Ready";
            ShowcaseCapture.CaptureWindow(
                window, Path.Combine(ShowcaseCapture.OutputDirectory(), "waveguide-editing.png"));
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    [AvaloniaFact]
    public async Task CaptureNetlistExport()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            await ShowcaseCircuit.BuildChipAsync(vm);
            vm.RightPanel.Netlist.RefreshCommand.Execute(null);
            vm.RightPanel.Netlist.NetlistYaml.ShouldContain("instances:");
            vm.RightPanel.Netlist.NetlistYaml.ShouldContain("connections:");

            var window = BuildNetlistWindow(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ShowcaseCapture.CaptureWindow(
                window, Path.Combine(ShowcaseCapture.OutputDirectory(), "netlist-export.png"));
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// <summary>The netlist view: the real right-panel Netlist section next to the generated
    /// gdsfactory YAML — instances on the left, connections/ports on the right.</summary>
    private static Window BuildNetlistWindow(MainViewModel vm)
    {
        var yaml = vm.RightPanel.Netlist.NetlistYaml;
        int connectionsAt = yaml.IndexOf("connections:", StringComparison.Ordinal);
        var panel = new NetlistPanel
        {
            DataContext = vm,
            Margin = new global::Avalonia.Thickness(10, 0, 10, 10),
            Width = 330,
        };
        var grid = new AvGrid { ColumnDefinitions = new ColumnDefinitions("350,*,*") };
        grid.Children.Add(panel);
        AvGrid.SetColumn(panel, 0);
        var instancesPane = YamlPane(yaml[..Math.Max(connectionsAt, 0)]);
        var connectionsPane = YamlPane(yaml[Math.Max(connectionsAt, 0)..]);
        grid.Children.Add(instancesPane);
        grid.Children.Add(connectionsPane);
        AvGrid.SetColumn(instancesPane, 1);
        AvGrid.SetColumn(connectionsPane, 2);

        return new Window
        {
            Width = 1380,
            Height = 780,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Content = grid,
        };
    }

    private static TextBox YamlPane(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
        FontSize = 12.5,
        Margin = new global::Avalonia.Thickness(6, 10, 6, 10),
    };
}
