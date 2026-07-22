using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export.Netlist;
using CAP_Core.Components.Core;
using CAP_Core.Tiles;
using Moq;
using Shouldly;

namespace UnitTests.Export.Netlist;

/// <summary>
/// The Netlist panel ViewModel must generate YAML from the canvas, report status,
/// and support copy/save flows (issue #687).
/// </summary>
public class NetlistViewModelTests
{
    /// <summary>Pin the UI language so status-text assertions match the English literals
    /// regardless of the runner's locale (LocalizationService.Instance is process-wide).</summary>
    public NetlistViewModelTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    private static DesignCanvasViewModel MakeCanvasWithComponent()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = TestComponentFactory.CreateBasicComponent();
        comp.Identifier = "A";
        comp.NazcaFunctionName = "demo_pdk.mmi";
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "o1",
            ParentComponent = comp,
            LogicalPin = new Pin("o1", 0, MatterType.Light, RectSide.Left),
        });
        canvas.AddComponent(comp, "Test");
        return canvas;
    }

    [Fact]
    public void Refresh_EmptyCanvas_ReportsNothingToExport()
    {
        var vm = new NetlistViewModel();
        vm.Configure(new DesignCanvasViewModel());

        vm.RefreshCommand.Execute(null);

        vm.HasNetlist.ShouldBeFalse();
        vm.StatusText.ShouldContain("Nothing to export");
    }

    [Fact]
    public void Refresh_CanvasWithComponent_GeneratesYamlAndStatus()
    {
        var vm = new NetlistViewModel();
        vm.Configure(MakeCanvasWithComponent());

        vm.RefreshCommand.Execute(null);

        vm.HasNetlist.ShouldBeTrue();
        vm.NetlistYaml.ShouldContain("instances:");
        vm.NetlistYaml.ShouldContain("component: demo_pdk.mmi");
        vm.StatusText.ShouldContain("1 instances");
    }

    [Fact]
    public async Task CopyYaml_WithClipboardCallback_CopiesGeneratedYaml()
    {
        var vm = new NetlistViewModel();
        vm.Configure(MakeCanvasWithComponent());
        string? copied = null;
        vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await vm.CopyYamlCommand.ExecuteAsync(null);

        copied.ShouldNotBeNull();
        copied.ShouldContain("instances:");
        vm.StatusText.ShouldContain("copied");
    }

    [Fact]
    public async Task SaveYaml_WithDialogPath_WritesFile()
    {
        var vm = new NetlistViewModel();
        vm.Configure(MakeCanvasWithComponent());
        var path = Path.Combine(Path.GetTempPath(), $"netlist-test-{Guid.NewGuid()}.yml");
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(d => d.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        vm.FileDialogService = dialog.Object;

        try
        {
            await vm.SaveYamlCommand.ExecuteAsync(null);

            File.Exists(path).ShouldBeTrue();
            (await File.ReadAllTextAsync(path)).ShouldContain("instances:");
            vm.StatusText.ShouldContain("Exported netlist");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveYaml_DialogCancelled_ReportsCancellation()
    {
        var vm = new NetlistViewModel();
        vm.Configure(MakeCanvasWithComponent());
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(d => d.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        vm.FileDialogService = dialog.Object;

        await vm.SaveYamlCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("Export cancelled");
    }
}
