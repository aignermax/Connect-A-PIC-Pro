using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>Dialog-flow tests for the mixed-backend GDS export: the nazca partial
/// script is written and run first, then the main gdsfactory script merges its GDS.</summary>
[Trait("Category", "Slow")]
public class MixedBackendExportViewModelTests
{
    /// <summary>Pin the UI language so status-text assertions match the English literals.</summary>
    public MixedBackendExportViewModelTests()
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

    /// <summary>Records every script run in order; succeeds or fails per delegate.</summary>
    private sealed class RecordingExportService : GdsExportService
    {
        private readonly Func<string, bool> _succeeds;
        public List<string> RunScripts { get; } = new();

        public RecordingExportService(Func<string, bool>? succeeds = null) =>
            _succeeds = succeeds ?? (_ => true);

        public override Task<ExportResult> ExportToGdsAsync(string scriptPath, bool generateGds)
        {
            RunScripts.Add(scriptPath);
            var ok = _succeeds(scriptPath);
            return Task.FromResult(new ExportResult
            {
                ScriptPath = scriptPath,
                Success = ok,
                ErrorMessage = ok ? null : "nazca boom",
            });
        }
    }

    private static DesignCanvasViewModel MixedBackendCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var gf = TestComponentFactory.CreateBasicComponent();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(gf, "SiN MMI");
        var nazca = TestComponentFactory.CreateBasicComponent();
        nazca.Identifier = "NZ1";
        nazca.NazcaFunctionName = "ebeam_y_1550";
        canvas.AddComponent(nazca, "Y-Branch");
        return canvas;
    }

    [Fact]
    public async Task Export_MixedBackendDesign_WritesBothScriptsAndRunsPartialFirst()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfmx-{Guid.NewGuid():N}.py");
        var partialPath = MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            var service = new RecordingExportService();
            var vm = new GdsFactoryExportViewModel(MixedBackendCanvas(), service)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();
            File.Exists(partialPath).ShouldBeTrue();
            // The nazca partial must run FIRST — its GDS is merged by the main script.
            service.RunScripts.ShouldBe(new[] { partialPath, scriptPath });

            var mainScript = await File.ReadAllTextAsync(scriptPath);
            mainScript.ShouldContain("gf.import_gds");
            mainScript.ShouldNotContain("ebeam_y_1550");
            var partialScript = await File.ReadAllTextAsync(partialPath);
            partialScript.ShouldContain("ebeam_y_1550");
            partialScript.ShouldContain(MixedBackendGdsOrchestrator.NazcaPartialTopCellName);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    [Fact]
    public async Task Export_NazcaPartialFails_StopsBeforeMainScriptWithClearStatus()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfmx-{Guid.NewGuid():N}.py");
        var partialPath = MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var service = new RecordingExportService(succeeds: _ => false);
            var vm = new GdsFactoryExportViewModel(
                MixedBackendCanvas(), service, errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            // The main script must not run against a missing/stale partial GDS.
            service.RunScripts.ShouldBe(new[] { partialPath });
            vm.StatusText.ShouldContain("nazca part failed");
            vm.IsExporting.ShouldBeFalse();
            errorConsole.Entries.ShouldContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Error
                && e.Message.Contains("nazca partial failed"));
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    /// <summary>
    /// Same mixed-backend layout as <see cref="MixedBackendCanvas"/>, plus one routed
    /// connection between the two components whose <see cref="RoutedPath"/> is an honest
    /// placeholder (self-crossing fallback, no optical model). All routed connections are
    /// owned by the gdsfactory script (the nazca partial only renders placements), so this
    /// connection is collected into <c>skippedConnections</c> while <c>BuildScripts</c> writes
    /// the main script — BEFORE the nazca partial is even run.
    /// </summary>
    private static DesignCanvasViewModel MixedBackendCanvasWithSkippedConnection()
    {
        var canvas = new DesignCanvasViewModel();

        var gf = TestComponentFactory.CreateBasicComponent();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        var gfPin = new PhysicalPin
        {
            Name = "east0",
            ParentComponent = gf,
            OffsetXMicrometers = gf.WidthMicrometers,
            OffsetYMicrometers = gf.HeightMicrometers / 2,
            AngleDegrees = 0,
            LogicalPin = gf.Parts[0, 0].GetPinAt(RectSide.Right),
        };
        gf.PhysicalPins.Add(gfPin);
        canvas.AddComponent(gf, "SiN MMI");

        var nazca = TestComponentFactory.CreateBasicComponent();
        nazca.Identifier = "NZ1";
        nazca.NazcaFunctionName = "ebeam_y_1550";
        nazca.PhysicalX = gf.WidthMicrometers + 100;
        var nazcaPin = new PhysicalPin
        {
            Name = "west0",
            ParentComponent = nazca,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = nazca.HeightMicrometers / 2,
            AngleDegrees = 180,
            LogicalPin = nazca.Parts[0, 0].GetPinAt(RectSide.Left),
        };
        nazca.PhysicalPins.Add(nazcaPin);
        canvas.AddComponent(nazca, "Y-Branch");

        var connection = new WaveguideConnection { StartPin = gfPin, EndPin = nazcaPin };
        connection.ReplaceRoutedPath(new RoutedPath { IsPlaceholderGeometry = true });
        canvas.Connections.Add(new WaveguideConnectionViewModel(connection));

        return canvas;
    }

    /// <summary>
    /// Finding [4] (Round 3 delta-review): when the nazca partial fails AFTER both scripts
    /// are already written, the skipped-connection warning must not be lost alongside the
    /// early return — both the failure message and the warning belong in the same status.
    /// </summary>
    [Fact]
    public async Task Export_NazcaPartialFailsWithSkippedConnection_ReportsBothFailureAndWarning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfmx-{Guid.NewGuid():N}.py");
        var partialPath = MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var service = new RecordingExportService(succeeds: _ => false);
            var vm = new GdsFactoryExportViewModel(
                MixedBackendCanvasWithSkippedConnection(), service, errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            service.RunScripts.ShouldBe(new[] { partialPath });
            vm.StatusText.ShouldContain("nazca part failed");
            vm.StatusText.ShouldContain("1 connection(s)");
            vm.StatusText.ShouldContain("GF1"); // one endpoint of the skipped connection
            errorConsole.Entries.ShouldContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Warn
                && e.Message.Contains("1 connection(s)"));
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    /// <summary>
    /// Finding: the mixed-backend export must surface raw-code missing-source fallbacks
    /// (a deleted .gds → placeholder box in the nazca partial) on the aggregated status
    /// line, not only in the Error Console — the details stay in the console, the count
    /// rides the final status.
    /// </summary>
    [Fact]
    public async Task Export_MixedBackendWithMissingGdsSource_WarnsInConsoleAndStatus()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfmx-{Guid.NewGuid():N}.py");
        var partialPath = MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            var missingGds = Path.Combine(Path.GetTempPath(), $"deleted-{Guid.NewGuid():N}.gds"); // never written
            var errorConsole = new CAP_Core.ErrorConsoleService();
            var service = new RecordingExportService();
            var vm = new GdsFactoryExportViewModel(
                MixedBackendCanvasWithRawCodeComponent(), service, errorConsole: errorConsole)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
                TemplateLibraryProvider = () => new[] { RawCodeTemplate(missingGds) },
            };

            await vm.ExportCommand.ExecuteAsync(null);

            // Detailed per-component description → Error Console (as before)…
            errorConsole.Entries.ShouldContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Warn
                && e.Message.Contains("wgA")
                && e.Message.Contains(missingGds));
            // …and the aggregated count on the final status line (the new part).
            vm.StatusText.ShouldContain("1 component(s)");
            vm.StatusText.ShouldContain("placeholder box");
            // The nazca partial genuinely got the fallback box under the wrapper name.
            var partialScript = await File.ReadAllTextAsync(partialPath);
            partialScript.ShouldContain("component_wgA");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    /// <summary>The mixed-backend layout of <see cref="MixedBackendCanvas"/>, but the
    /// nazca side is a raw-code component whose template loads a missing .gds.</summary>
    private static DesignCanvasViewModel MixedBackendCanvasWithRawCodeComponent()
    {
        var canvas = new DesignCanvasViewModel();
        var gf = TestComponentFactory.CreateBasicComponent();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(gf, "SiN MMI");

        var raw = TestComponentFactory.CreateBasicComponent();
        raw.Identifier = "WG1";
        raw.NazcaFunctionName = "nazca_wga";
        raw.PhysicalX = 400;
        raw.PhysicalPins.Add(new PhysicalPin
        {
            Name = "in", ParentComponent = raw,
            OffsetXMicrometers = 0, OffsetYMicrometers = 125, AngleDegrees = 180,
        });
        raw.PhysicalPins.Add(new PhysicalPin
        {
            Name = "out", ParentComponent = raw,
            OffsetXMicrometers = 250, OffsetYMicrometers = 125, AngleDegrees = 0,
        });
        canvas.AddComponent(raw, "wgA");
        return canvas;
    }

    /// <summary>A nazca-backend raw-code template shaped like a GDS import, loading a
    /// .gds file that does not exist (the fallback path under test).</summary>
    private static ComponentTemplate RawCodeTemplate(string missingGdsPath) => new()
    {
        Name = "wgA",
        PdkSource = "GDS Import - circuit",
        WidthMicrometers = 250,
        HeightMicrometers = 250,
        PinDefinitions = new[]
        {
            new PinDefinition("in", 0, 125, 180),
            new PinDefinition("out", 250, 125, 0),
        },
        RawCode =
            "import nazca as nd\n" +
            "\n" +
            "def component():\n" +
            "    with nd.Cell(name=\"wgA_aligned\") as cell:\n" +
            $"        _loaded = nd.load_gds(filename=\"{missingGdsPath.Replace("\\", "\\\\")}\", cellname=\"wgA\", topcellsonly=False)\n" +
            "        _bb = _loaded.bbox\n" +
            "        _loaded.put(-_bb[0], -_bb[1])\n" +
            "    return cell\n",
        RawCodeBackend = "nazca",
    };

    [Fact]
    public async Task Export_SingleGdsFactoryBackendDesign_WritesNoPartialScript()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gfmx-{Guid.NewGuid():N}.py");
        var partialPath = MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            var canvas = new DesignCanvasViewModel();
            var gf = TestComponentFactory.CreateBasicComponent();
            gf.NazcaFunctionName = "";
            gf.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
            canvas.AddComponent(gf, "SiN MMI");
            var service = new RecordingExportService();
            var vm = new GdsFactoryExportViewModel(canvas, service)
            {
                FileDialogService = new FixedPathFileDialog(scriptPath),
            };

            await vm.ExportCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();
            File.Exists(partialPath).ShouldBeFalse();   // single-backend gdsfactory: one-script path
            service.RunScripts.ShouldBe(new[] { scriptPath });
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }
}
