using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP_Core.Export;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport;

/// <summary>Tests for the gdsfactory export dialog ViewModel (#581).</summary>
public class GdsFactoryExportViewModelTests
{
    private sealed class FixedPathFileDialog : IFileDialogService
    {
        private readonly string? _path;
        public FixedPathFileDialog(string? path) => _path = path;

        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters) =>
            Task.FromResult(_path);

        public Task<string?> ShowOpenFileDialogAsync(string title, string filters) =>
            Task.FromResult<string?>(null);
    }

    private static DesignCanvasViewModel CanvasWithComponent(string nazcaFunction)
    {
        var canvas = new DesignCanvasViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = "C1";
        component.NazcaFunctionName = nazcaFunction;
        canvas.AddComponent(component, nazcaFunction);
        return canvas;
    }

    [Fact]
    public async Task Export_EmptyCanvas_ExplainsAndWritesNothing()
    {
        var vm = new GdsFactoryExportViewModel(new DesignCanvasViewModel(), new GdsExportService())
        {
            FileDialogService = new FixedPathFileDialog("unused.py"),
        };

        await vm.ExportCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("Nothing to export");
    }

    [Fact]
    public async Task Export_ShadowingFileName_IsRefused()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "re.py");
        var vm = new GdsFactoryExportViewModel(CanvasWithComponent("ebeam_y_1550"), new GdsExportService())
        {
            FileDialogService = new FixedPathFileDialog(scriptPath),
        };

        await vm.ExportCommand.ExecuteAsync(null);

        File.Exists(scriptPath).ShouldBeFalse();
        vm.StatusText.ShouldContain("shadows");
    }

    [Fact]
    public async Task Export_WithoutGdsGeneration_WritesScriptInSelectedMode()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        try
        {
            var vm = new GdsFactoryExportViewModel(CanvasWithComponent("ebeam_y_1550"), new GdsExportService())
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
                GenerateGdsEnabled = false,
                UseUbcPdkCells = true,
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();
            var script = await File.ReadAllTextAsync(scriptPath);
            script.ShouldContain("gf.get_component('ebeam_y_1550')");   // ubcpdk mode was honored
            vm.StatusText.ShouldContain("GDS generation skipped");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public void BuildFailureMessage_MissingGdsFactory_GuidesToInstallWithoutTraceback()
    {
        var traceback = "Python script execution failed (exit code 1): Traceback ...\n"
            + "ModuleNotFoundError: No module named 'gdsfactory'";

        var msg = GdsFactoryExportViewModel.BuildFailureMessage("test.py", traceback);

        msg.ShouldContain("gdsfactory is not installed");
        msg.ShouldContain("Install gdsfactory");
        msg.ShouldNotContain("Traceback");         // no raw error in the dialog line
        msg.ShouldNotContain("ModuleNotFoundError");
    }

    [Fact]
    public void BuildFailureMessage_OtherError_PointsToErrorConsoleWithoutTraceback()
    {
        var msg = GdsFactoryExportViewModel.BuildFailureMessage(
            "test.py", "Traceback ...\nSomeOtherError: boom");

        msg.ShouldContain("Error Console");
        msg.ShouldNotContain("Traceback");
        msg.ShouldNotContain("boom");
    }

    [Fact]
    public void RefreshUnmappedComponents_ListsOnlyUnmapped()
    {
        var vm = new GdsFactoryExportViewModel(CanvasWithComponent("ebeam_dc_te1550"), new GdsExportService());

        vm.RefreshUnmappedComponents();

        vm.UnmappedComponents.ShouldBe(new[] { "ebeam_dc_te1550" });
    }
}
