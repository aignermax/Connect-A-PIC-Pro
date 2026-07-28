using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
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

    private static DesignCanvasViewModel CanvasWithGdsFactoryComponent(string gdsFactoryFunction)
    {
        var canvas = new DesignCanvasViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = "C1";
        component.NazcaFunctionName = "";
        component.GdsFactoryFunction = gdsFactoryFunction;
        canvas.AddComponent(component, gdsFactoryFunction);
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
    public async Task Export_UbcPdkMappableNazcaComponent_RendersViaNazcaPartialNotUbcPdk()
    {
        // ubcpdk-mappable nazca components (ebeam_*) take the nazca partial — the real
        // foundry cell from siepic_ebeam_pdk — not the ubcpdk substitute in the
        // gdsfactory script.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        var partialPath = CAP.Avalonia.Services.GdsFactoryExport.MixedBackend
            .MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            var vm = new GdsFactoryExportViewModel(CanvasWithComponent("ebeam_y_1550"), new GdsExportService())
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();
            File.Exists(partialPath).ShouldBeTrue();
            (await File.ReadAllTextAsync(scriptPath)).ShouldNotContain("gf.get_component('ebeam_y_1550')");
            (await File.ReadAllTextAsync(partialPath)).ShouldContain("ebeam_y_1550");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
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
                CanvasWithGdsFactoryComponent("cspdk.sin300.mmi1x2"),
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
    public async Task Export_MixedBackendDesign_WarnsAndMergesInsteadOfRefusing()
    {
        // A design mixing a gdsfactory-native and a nazca-native component takes
        // the two-script merge path: each backend renders its own group, the main gdsfactory
        // script merges the nazca partial GDS. Because this canvas ALSO mixes fabrication
        // processes (CornerStone SiN + SiEPIC SOI), the user gets the strong
        // not-manufacturable warning (dialog AND Error Console).
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        var partialPath = CAP.Avalonia.Services.GdsFactoryExport.MixedBackend
            .MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
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
            File.Exists(partialPath).ShouldBeTrue();  // nazca partial written next to it
            var script = await File.ReadAllTextAsync(scriptPath);
            script.ShouldContain("cspdk.sin300.PDK.activate()");
            script.ShouldContain("gf.import_gds");    // merges the nazca partial GDS
            script.ShouldNotContain("ebeam_y_1550");  // nazca-native group left to nazca
            // The strong mixed-process warning stays visible in the final dialog status,
            // next to the result.
            vm.StatusText.ShouldContain("NOT manufacturable");
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
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    [Fact]
    public async Task Export_AllNazcaDesign_TakesTwoScriptPathWithoutMergeNotice()
    {
        // An all-nazca design (e.g. a pure demo-PDK circuit) takes the two-script path so
        // its components render with nazca instead of gdsfactory stubs. The merge notice
        // stays away — nothing is mixed.
        var canvas = new DesignCanvasViewModel();
        var demo = TestComponentFactory.CreateBasicComponent();
        demo.Identifier = "D1";
        demo.NazcaFunctionName = "demo.mmi2x2_dp";
        canvas.AddComponent(demo, "Demo MMI");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        var partialPath = CAP.Avalonia.Services.GdsFactoryExport.MixedBackend
            .MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var vm = new GdsFactoryExportViewModel(
                canvas, new StubSuccessExportService(), errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();
            File.Exists(partialPath).ShouldBeTrue();   // two-script path even with an empty gf group
            (await File.ReadAllTextAsync(partialPath)).ShouldContain("demo.mmi2x2_dp");
            (await File.ReadAllTextAsync(scriptPath)).ShouldContain("gf.import_gds");
            vm.StatusText.ShouldNotContain("merged into one GDS");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    [Fact]
    public void RefreshUnmappedComponents_DoesNotListPartialRenderedComponents()
    {
        // demo/demofab and SiEPIC components render real via the nazca partial — the
        // stub-geometry warning must not list them; a bare gdsfactory name still stubs.
        var canvas = new DesignCanvasViewModel();
        var demo = TestComponentFactory.CreateBasicComponent();
        demo.NazcaFunctionName = "demo.mmi2x2_dp";
        canvas.AddComponent(demo, "Demo MMI");
        var siepic = TestComponentFactory.CreateBasicComponent();
        siepic.NazcaFunctionName = "ebeam_gc_te895";
        siepic.NazcaModuleName = "siepic_ebeam_pdk";
        canvas.AddComponent(siepic, "GC");
        var bare = TestComponentFactory.CreateBasicComponent();
        bare.NazcaFunctionName = "my_custom_cell";
        canvas.AddComponent(bare, "Custom");

        var vm = new GdsFactoryExportViewModel(canvas, new StubSuccessExportService());
        vm.RefreshUnmappedComponents();

        vm.UnmappedComponents.ShouldNotContain("demo.mmi2x2_dp");
        vm.UnmappedComponents.ShouldNotContain("ebeam_gc_te895");
        vm.UnmappedComponents.ShouldContain("my_custom_cell");
    }

    [Fact]
    public async Task Export_SameProcessMixedBackendDesign_GetsSoftMergeNotice()
    {
        // A mixed-backend design WITHOUT a process conflict (plain gdsfactory built-ins
        // carry no process module, so no conflict is detectable) gets the neutral merge
        // notice instead of the strong not-manufacturable warning.
        var canvas = new DesignCanvasViewModel();
        var gf = TestComponentFactory.CreateBasicComponent();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "mmi2x2";   // gdsfactory built-in — no module, no process
        canvas.AddComponent(gf, "MMI");
        var nz = TestComponentFactory.CreateBasicComponent();
        nz.Identifier = "NZ1";
        nz.NazcaFunctionName = "ebeam_y_1550";
        canvas.AddComponent(nz, "Y-Branch");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        var partialPath = CAP.Avalonia.Services.GdsFactoryExport.MixedBackend
            .MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var vm = new GdsFactoryExportViewModel(
                canvas, new StubSuccessExportService(), errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();
            File.Exists(partialPath).ShouldBeTrue();
            vm.StatusText.ShouldContain("merged into one GDS");
            vm.StatusText.ShouldNotContain("NOT manufacturable");
            errorConsole.Entries.ShouldContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Warn
                && e.Message.Contains("mixes gdsfactory-native and nazca-native"));
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
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

    /// <summary>Two components joined by a connection whose route is an honest placeholder
    /// (the router's replacement for a self-crossing fallback with no optical model) — export
    /// must still succeed but leave that geometry out. Both components carry a (bare)
    /// gdsfactory function so the export takes the plain single-script path rather than the
    /// nazca-native two-script merge.</summary>
    private static DesignCanvasViewModel CanvasWithPlaceholderConnection()
    {
        var canvas = new DesignCanvasViewModel();
        var a = TestComponentFactory.CreateBasicComponent();
        a.Identifier = "CompA";
        a.NazcaFunctionName = "";
        a.GdsFactoryFunction = "mmi2x2";
        a.PhysicalPins.Add(new PhysicalPin { Name = "p0", ParentComponent = a });
        var b = TestComponentFactory.CreateBasicComponent();
        b.Identifier = "CompB";
        b.NazcaFunctionName = "";
        b.GdsFactoryFunction = "mmi2x2";
        b.PhysicalX = 200;
        b.PhysicalPins.Add(new PhysicalPin { Name = "p0", ParentComponent = b });
        canvas.AddComponent(a, "MMI A");
        canvas.AddComponent(b, "MMI B");

        var connection = new WaveguideConnection { StartPin = a.PhysicalPins[0], EndPin = b.PhysicalPins[0] };
        var path = new RoutedPath { IsPlaceholderGeometry = true };
        path.Segments.Add(new StraightSegment(0, 0, 1, 0, 0));
        connection.RestoreCachedPath(path);
        canvas.Connections.Add(new WaveguideConnectionViewModel(connection));
        return canvas;
    }

    [Fact]
    public async Task Export_PlaceholderConnection_WarnsAndOmitsItFromTheGds()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        try
        {
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var vm = new GdsFactoryExportViewModel(
                CanvasWithPlaceholderConnection(), new StubSuccessExportService(), errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();   // export still ran
            (await File.ReadAllTextAsync(scriptPath)).ShouldNotContain("gf.components.straight(");
            vm.StatusText.ShouldContain("1 connection(s)");
            vm.StatusText.ShouldContain("CompA.p0");
            vm.StatusText.ShouldContain("CompB.p0");
            errorConsole.Entries.ShouldContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Warn
                && e.Message.Contains("CompA.p0")
                && e.Message.Contains("CompB.p0"));
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task Export_AllConnectionsValid_NoSkippedConnectionsWarning()
    {
        var canvas = CanvasWithPlaceholderConnection();
        // Replace the placeholder path with a valid one so nothing is skipped.
        var connection = canvas.Connections[0].Connection;
        connection.RestoreCachedPath(new RoutedPath { Segments = { new StraightSegment(0, 0, 1, 0, 0) } });

        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfvm-{Guid.NewGuid():N}.py");
        try
        {
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var vm = new GdsFactoryExportViewModel(
                canvas, new StubSuccessExportService(), errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            vm.StatusText.ShouldNotContain("connection(s)");
            errorConsole.Entries.ShouldBeEmpty();
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task Export_Cancelled_NoSkippedConnectionsWarning()
    {
        // Cancel (Save dialog dismissed) means no script is ever written — the skip report
        // must not be computed/logged for a design snapshot that was never exported.
        var errorConsole = new CAP_Core.ErrorConsoleService();
        var vm = new GdsFactoryExportViewModel(
            CanvasWithPlaceholderConnection(), new StubSuccessExportService(), errorConsole: errorConsole)
        {
            FileDialogService = new FixedPathFileDialog(null),
        };

        await vm.ExportCommand.ExecuteAsync(null);

        vm.StatusText.ShouldNotContain("connection(s)");
        errorConsole.Entries.ShouldBeEmpty();
    }
}
