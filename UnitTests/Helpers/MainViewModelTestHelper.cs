using System.IO;
using System.Net.Http;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Update;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;
using CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;
using CAP.Avalonia.ViewModels.Analysis.OnaAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.ViewModels.Properties;
using CAP.Avalonia.ViewModels.Properties.Editors;
using CAP.Avalonia.ViewModels.Update;
using CAP.Avalonia.ViewModels.AI;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_Core.Components.Creation;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Moq;

namespace UnitTests.Helpers;

/// <summary>
/// Factory helpers for creating <see cref="MainViewModel"/> instances in tests.
/// Provides minimal but valid dependencies for all sub-ViewModels.
/// </summary>
public static class MainViewModelTestHelper
{
    /// <summary>
    /// Creates a fully wired <see cref="MainViewModel"/> with default test dependencies.
    /// </summary>
    public static MainViewModel CreateMainViewModel(
        SimulationService? simulationService = null,
        CommandManager? commandManager = null,
        UserPreferencesService? preferencesService = null,
        GroupLibraryManager? libraryManager = null,
        DesignCanvasViewModel? canvas = null,
        LeftPanelViewModel? leftPanel = null)
    {
        canvas ??= new DesignCanvasViewModel();
        commandManager ??= new CommandManager();
        // Isolated temp-file prefs so AiAssistant auto-persist and every
        // other *Changed handler cannot clobber the developer's real file.
        preferencesService ??= new UserPreferencesService(
            Path.Combine(Path.GetTempPath(), $"cap-test-prefs-{Guid.NewGuid()}.json"));
        libraryManager ??= new GroupLibraryManager();
        simulationService ??= new SimulationService();

        var pdkLoader = new PdkLoader();
        // A caller-supplied LeftPanel (UI-flow tests) must share canvas/prefs with the rest of the VM.
        leftPanel ??= CreateLeftPanelViewModel(canvas, libraryManager, pdkLoader, preferencesService, commandManager);
        var rightPanel = CreateRightPanelViewModel(canvas, preferencesService);
        var bottomPanel = CreateBottomPanelViewModel(canvas, commandManager);

        var errorConsoleService = new CAP_Core.ErrorConsoleService();
        var gdsExportVm = new GdsExportViewModel(new GdsExportService(), errorConsoleService);
        var updateVm = new UpdateViewModel(
            new UpdateChecker(new HttpClient(), "aignermax", "Connect-A-PIC-Pro"),
            new UpdateDownloader(new HttpClient()),
            preferencesService,
            Mock.Of<IUrlLauncher>(),
            Mock.Of<IInstaller>());
        var photonTorchVm = new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas);
        var verilogAVm = new VerilogAExportViewModel(new VerilogAExporter(), new VerilogAFileWriter(), canvas);
        var gdsFactoryVm = new GdsFactoryExportViewModel(canvas, new GdsExportService(), errorConsole: errorConsoleService);
        // Test-isolated design-scoped GDS components (#830): registers imported
        // sets on the LeftPanel and caches .gds bytes in a unique temp dir so a
        // UI-flow import never touches the developer's real cache or PDKs.
        var capturedLeftPanel = leftPanel;
        var designScope = new CAP.Avalonia.Services.GdsImport.DesignScope.DesignScopedGdsComponentService(
            capturedLeftPanel.RegisterDesignScopedPdk,
            capturedLeftPanel.RemoveDesignScopedPdk,
            Path.Combine(Path.GetTempPath(), $"cap-test-gds-cache-{Guid.NewGuid()}"));
        var gdsImportButton = new CAP.Avalonia.ViewModels.GdsImport.GdsImportButtonViewModel(
            new CAP.Avalonia.Services.GdsImport.GdsImportService(
                designScope,
                () => capturedLeftPanel.AllTemplates.ToList()),
            new CAP.Avalonia.Services.GdsImport.GdsPlacementExecutor(
                canvas, commandManager, () => capturedLeftPanel.AllTemplates.ToList()),
            errorConsoleService);

        return new MainViewModel(
            canvas,
            simulationService,
            new SimpleNazcaExporter(),
            new CAP_Core.Export.SaxExporter(),
            commandManager,
            preferencesService,
            new GroupPreviewGenerator(),
            Mock.Of<IInputDialogService>(),
            errorConsoleService,
            gdsExportVm,
            updateVm,
            leftPanel,
            rightPanel,
            bottomPanel,
            new ViewportControlViewModel(canvas),
            new PdkOffsetEditorViewModel(pdkLoader, new PdkJsonSaver(), new PdkManagerViewModel()),
            photonTorchVm,
            verilogAVm,
            gdsFactoryVm,
            new CAP.Avalonia.ViewModels.Canvas.ChipSizeViewModel(preferencesService, canvas),
            // Test-isolated user S-matrix store: a unique temp path per call so
            // tests don't contaminate each other or the developer's real file.
            new CAP.Avalonia.Services.UserSMatrixOverrideStore(
                Path.Combine(Path.GetTempPath(), $"sparam-overrides-test-{Guid.NewGuid()}.json")),
            new GdsPreviewRenderService(new NazcaComponentPreviewService("python3", "/nonexistent/script.py")),
            // Registry browser backed by the committed fixtures — no network access.
            new CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser.RegistryBrowserViewModel(
                new UnitTests.ComponentRegistry.RegistryClient.RegistryTestHarness().CreateClient()),
            gdsImportButton,
            designScopedGdsComponents: designScope);
    }

    /// <summary>
    /// Creates a <see cref="LeftPanelViewModel"/> with all required sub-VM dependencies.
    /// </summary>
    public static LeftPanelViewModel CreateLeftPanelViewModel(
        DesignCanvasViewModel? canvas = null,
        GroupLibraryManager? libraryManager = null,
        PdkLoader? pdkLoader = null,
        UserPreferencesService? preferencesService = null,
        CommandManager? commandManager = null)
    {
        canvas ??= new DesignCanvasViewModel();
        libraryManager ??= new GroupLibraryManager();
        pdkLoader ??= new PdkLoader();
        // Isolated temp-file prefs — the real file must never be touched by tests.
        preferencesService ??= new UserPreferencesService(
            Path.Combine(Path.GetTempPath(), $"cap-test-prefs-{Guid.NewGuid()}.json"));

        var leftPanel = new LeftPanelViewModel(
            canvas,
            libraryManager,
            pdkLoader,
            preferencesService,
            new HierarchyPanelViewModel(canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager));

        // Isolate from the developer's real user-PDK folder: the startup reload must scan an
        // empty temp dir, otherwise template counts/status texts become machine-dependent
        // (UiFlowTestHost already isolates this way; CI is green only because its folder is empty).
        var isolatedUserPdkRoot = Path.Combine(Path.GetTempPath(), $"cap-test-userpdks-{Guid.NewGuid()}");
        Directory.CreateDirectory(isolatedUserPdkRoot);
        leftPanel.UserPdkStartupRootOverride = isolatedUserPdkRoot;

        return leftPanel;
    }

    /// <summary>
    /// Creates a <see cref="RightPanelViewModel"/> with all required sub-VM dependencies.
    /// </summary>
    public static RightPanelViewModel CreateRightPanelViewModel(
        DesignCanvasViewModel? canvas = null,
        UserPreferencesService? preferencesService = null)
    {
        canvas ??= new DesignCanvasViewModel();
        preferencesService ??= new UserPreferencesService(
            Path.Combine(Path.GetTempPath(), $"cap-test-prefs-{Guid.NewGuid()}.json"));

        return new RightPanelViewModel(
            canvas,
            preferencesService,
            new ParameterSweepViewModel(),
            new RoutingDiagnosticsViewModel(),
            new DesignValidationViewModel(),
            new ComponentDimensionDiagnosticsViewModel(canvas),
            new ComponentDimensionViewModel(),
            new ExportValidationViewModel(),
            new SMatrixPerformanceViewModel(),
            new CompressLayoutViewModel(),
            new GroupSMatrixViewModel(),
            new ArchitectureReportViewModel(),
            new PdkConsistencyViewModel(),
            new AiAssistantViewModel(Mock.Of<IAiService>(), preferencesService),
            new OnaSweepViewModel(),
            new CAP.Avalonia.ViewModels.Export.Netlist.NetlistViewModel(),
            // Production provider order (CanvasAndPanelExtensions): most specific
            // first, generic fallback last — so panel tests see real editors.
            new ComponentEditorFactory(new IComponentEditorProvider[]
            {
                new OnaAnalyzerEditorProvider(),
                new LightSourceEditorProvider(),
                new ParametricParametersEditorProvider(),
                new SliderEditorProvider(),
                new GenericComponentEditorProvider()
            }));
    }

    /// <summary>
    /// Creates a <see cref="BottomPanelViewModel"/> with all required sub-VM dependencies.
    /// </summary>
    public static BottomPanelViewModel CreateBottomPanelViewModel(
        DesignCanvasViewModel? canvas = null,
        CommandManager? commandManager = null)
    {
        canvas ??= new DesignCanvasViewModel();
        commandManager ??= new CommandManager();
        var errorConsoleService = new CAP_Core.ErrorConsoleService();

        return new BottomPanelViewModel(
            canvas,
            commandManager,
            new ConnectionRoutingViewModel(canvas),
            new CAP.Avalonia.ViewModels.Canvas.RerouteImported.RerouteImportedRoutesViewModel(canvas, commandManager),
            new ElementLockViewModel(),
            new ErrorConsoleViewModel(errorConsoleService),
            new AnalysisDockViewModel(
                new TimeDomainViewModel(),
                new EyeDiagramViewModel(),
                new WavelengthSpectrumViewModel(),
                new AnalysisOutputPanelViewModel(),
                new MonteCarloViewModel(),
                new CAP.Avalonia.ViewModels.Analysis.CircuitOptimization.CircuitOptimizationViewModel(new CommandManager())));
    }
}
