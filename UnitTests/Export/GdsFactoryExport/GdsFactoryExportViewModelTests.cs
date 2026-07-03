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
    public async Task Export_AlwaysWritesUbcPdkScript()
    {
        // No geometry question anymore: the export always uses ubcpdk cells where available
        // (stub fallback otherwise) and always attempts GDS generation.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        try
        {
            var vm = new GdsFactoryExportViewModel(CanvasWithComponent("ebeam_y_1550"), new GdsExportService())
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();
            var script = await File.ReadAllTextAsync(scriptPath);
            script.ShouldContain("gf.get_component('ebeam_y_1550')");   // real ubcpdk cell used
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
    public async Task Export_GdsFactoryMissing_TriggersAutoInstallThenRetries()
    {
        // When the GDS run reports "No module named 'gdsfactory'", the export must invoke the
        // auto-install delegate and retry — without asking the user.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        try
        {
            // A GdsExportService whose python has no gdsfactory yields the missing-module error;
            // here we only assert the delegate is invoked, so a stub export service that always
            // reports the missing-module error drives the path deterministically.
            var vm = new GdsFactoryExportViewModel(
                CanvasWithComponent("ebeam_y_1550"),
                new StubMissingGdsFactoryExportService())
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };
            var installCalls = 0;
            vm.EnsureGdsFactoryAsync = (_, _) => { installCalls++; return Task.FromResult(true); };

            await vm.ExportCommand.ExecuteAsync(null);

            installCalls.ShouldBe(1);   // auto-install was triggered, no user prompt
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    /// <summary>Export service that always reports the missing-gdsfactory error, to drive the
    /// auto-install path without a real Python.</summary>
    private sealed class StubMissingGdsFactoryExportService : GdsExportService
    {
        public override Task<ExportResult> ExportToGdsAsync(string scriptPath, bool generateGds) =>
            Task.FromResult(new ExportResult
            {
                ScriptPath = scriptPath,
                Success = false,
                ErrorMessage = "Python script execution failed (exit code 1): "
                    + "ModuleNotFoundError: No module named 'gdsfactory'",
            });
    }

    [Fact]
    public void RefreshUnmappedComponents_ListsOnlyUnmapped()
    {
        var vm = new GdsFactoryExportViewModel(CanvasWithComponent("ebeam_dc_te1550"), new GdsExportService());

        vm.RefreshUnmappedComponents();

        vm.UnmappedComponents.ShouldBe(new[] { "ebeam_dc_te1550" });
    }
}
