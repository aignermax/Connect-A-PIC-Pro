using System.IO;
using System.Net.Http;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Analysis;
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
        DesignCanvasViewModel? canvas = null)
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
        var leftPanel = CreateLeftPanelViewModel(canvas, libraryManager, pdkLoader, preferencesService, commandManager);
        var rightPanel = CreateRightPanelViewModel(canvas, preferencesService);
        var bottomPanel = CreateBottomPanelViewModel(canvas, commandManager);

        var errorConsoleService = new CAP_Core.ErrorConsoleService();
        var gdsExportVm = new GdsExportViewModel(new GdsExportService(), errorConsoleService);
        var updateVm = new UpdateViewModel(
            new UpdateChecker(new HttpClient(), "aignermax", "Connect-A-PIC-Pro"),
            new UpdateDownloader(new HttpClient()),
            preferencesService,
            Mock.Of<IUrlLauncher>());
        var photonTorchVm = new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas);
        var verilogAVm = new VerilogAExportViewModel(new VerilogAExporter(), new VerilogAFileWriter(), canvas);

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
            new CAP.Avalonia.ViewModels.Canvas.ChipSizeViewModel(preferencesService, canvas),
            // Test-isolated user S-matrix store: a unique temp path per call so
            // tests don't contaminate each other or the developer's real file.
            new CAP.Avalonia.Services.UserSMatrixOverrideStore(
                Path.Combine(Path.GetTempPath(), $"sparam-overrides-test-{Guid.NewGuid()}.json")),
            new GdsPreviewRenderService(new NazcaComponentPreviewService("python3", "/nonexistent/script.py")));
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
        preferencesService ??= new UserPreferencesService();

        return new LeftPanelViewModel(
            canvas,
            libraryManager,
            pdkLoader,
            preferencesService,
            new HierarchyPanelViewModel(canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager));
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
            new ComponentEditorFactory(new IComponentEditorProvider[]
            {
                new GenericComponentEditorProvider()
            }),
            new TimeDomainViewModel());
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
            new WaveguideLengthViewModel(),
            new ElementLockViewModel(),
            new ErrorConsoleViewModel(errorConsoleService));
    }
}
