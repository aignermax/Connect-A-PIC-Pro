using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export.GdsFactoryExport;

/// <summary>Tests for the gdsfactory export dialog ViewModel (#581).</summary>
[Trait("Category", "Slow")]
public class GdsFactoryExportViewModelTests
{
    /// <summary>Pin the UI language so status-text assertions match the English literals
    /// regardless of the runner's locale (LocalizationService.Instance is process-wide).</summary>
    public GdsFactoryExportViewModelTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

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

    /// <summary>Export service that reports script success without running Python, so the
    /// mixed-process tests stay deterministic and offline.</summary>
    private sealed class StubSuccessExportService : GdsExportService
    {
        public override Task<ExportResult> ExportToGdsAsync(string scriptPath, bool generateGds) =>
            Task.FromResult(new ExportResult { ScriptPath = scriptPath, Success = true });
    }

    private static DesignCanvasViewModel MixedProcessCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var sin = TestComponentFactory.CreateBasicComponent();
        sin.Identifier = "SIN1";
        sin.NazcaFunctionName = "";
        sin.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(sin, "SiN");
        var siepic = TestComponentFactory.CreateBasicComponent();
        siepic.Identifier = "EB1";
        siepic.NazcaFunctionName = "ebeam_y_1550";   // maps to a ubcpdk cell
        canvas.AddComponent(siepic, "Y-Branch");
        return canvas;
    }

    [Fact]
    public async Task Export_MixedProcessDesign_WarnsLoudlyButStillExports()
    {
        // Field decision (round 4): mixing processes no longer refuses the export. The GDS
        // is still generated (for inspection in the Playground) and the user gets an
        // unmissable warning in the dialog AND the Error Console that it is not fab-ready.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        try
        {
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var vm = new GdsFactoryExportViewModel(
                MixedProcessCanvas(), new StubSuccessExportService(), errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();   // export ran instead of refusing
            var script = await File.ReadAllTextAsync(scriptPath);
            script.ShouldContain("cspdk.sin300.PDK.activate()");
            script.ShouldContain("ubcpdk.PDK.activate()");
            // The warning stays visible in the final dialog status, next to the result.
            vm.StatusText.ShouldContain("NOT manufacturable");
            vm.StatusText.ShouldContain("cspdk.sin300");
            vm.StatusText.ShouldContain("Exported");
            // Logged as a WARNING (not an error) in the Error Console.
            errorConsole.Entries.ShouldContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Warn
                && e.Message.Contains("mixes fabrication processes"));
            errorConsole.Entries.ShouldNotContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Error);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task Export_SingleProcessDesign_HasNoMixedProcessWarning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        try
        {
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var vm = new GdsFactoryExportViewModel(
                CanvasWithComponent("ebeam_y_1550"), new StubSuccessExportService(),
                errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();
            vm.StatusText.ShouldNotContain("NOT manufacturable");
            errorConsole.Entries.ShouldNotContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Warn);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public void RefreshUnmappedComponents_ListsOnlyUnmapped()
    {
        var vm = new GdsFactoryExportViewModel(CanvasWithComponent("ebeam_dc_te1550"), new GdsExportService());

        vm.RefreshUnmappedComponents();

        vm.UnmappedComponents.ShouldBe(new[] { "ebeam_dc_te1550" });
    }
}
