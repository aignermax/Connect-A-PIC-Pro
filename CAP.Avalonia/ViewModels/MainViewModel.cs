using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Components.Core;
using CAP_Core;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.Services;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Simulation;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Export.Formats;
using CAP.Avalonia.ViewModels.Home;
using CAP.Avalonia.ViewModels.Update;
using CAP_Core.Export;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_Core.Components.Process;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// Main ViewModel that orchestrates all panel ViewModels.
/// Refactored to ~250 lines following CLAUDE.md guidelines.
/// Delegates responsibilities to specialized panel ViewModels.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private DesignCanvasViewModel _canvas;

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("Status.Ready");

    /// <summary>
    /// The last status set from a string-table key, with the text it produced — lets a live
    /// language switch re-translate the status bar when (and only when) it still shows that
    /// text, so transient messages (e.g. migration warnings) are never clobbered.
    /// </summary>
    private (string Key, object[] Args, string Formatted)? _lastLocalizedStatus =
        ("Status.Ready", [], LocalizationService.Instance.Translate("Status.Ready"));

    /// <summary>Application name shown in the window title.</summary>
    private const string AppTitle = "Lunima";

    /// <summary>Display name for a dirty design that has never been saved.</summary>
    private const string UntitledProjectName = "Untitled";

    /// <summary>Dirty-state marker appended to the file name in the window title.</summary>
    private const string DirtyMarker = "*";

    /// <summary>
    /// Window title derived from the open file and unsaved-changes state:
    /// "Lunima", "Untitled* — Lunima", "name.lun — Lunima", or "name.lun* — Lunima".
    /// </summary>
    [ObservableProperty]
    private string _windowTitle = AppTitle;

    /// <summary>
    /// Human-readable label for the design's active fabrication process (issue #570),
    /// e.g. "Process: Generic SOI 220 nm" or "Playground — not manufacturable". Kept in
    /// sync with <see cref="FileOperationsViewModel.ActiveProcess"/> by
    /// <see cref="RefreshProcessIndicator"/>. Bound by the toolbar indicator chip.
    /// </summary>
    [ObservableProperty]
    private string _activeProcessLabel = LocalizationService.Instance.Translate("Process.NoneSelected");

    /// <summary>
    /// True when the active process is Playground (mixing PDKs allowed, chip not
    /// manufacturable). Drives the warning styling on the toolbar indicator chip.
    /// </summary>
    [ObservableProperty]
    private bool _isPlayground;

    /// <summary>Active simulation mode; the toolbar selector binds here and Run(L) dispatches on it.</summary>
    [ObservableProperty]
    private CAP.Avalonia.ViewModels.Analysis.SimulationMode _simulationMode = CAP.Avalonia.ViewModels.Analysis.SimulationMode.Cw;

    /// <summary>
    /// Zero-based index view of <see cref="SimulationMode"/> (0 = Cw, 1 = Transient) for the
    /// toolbar <c>ComboBox</c>, which binds <c>SelectedIndex</c> directly with no converter.
    /// </summary>
    public int SimulationModeIndex
    {
        get => (int)SimulationMode;
        set => SimulationMode = (CAP.Avalonia.ViewModels.Analysis.SimulationMode)value;
    }

    /// <summary>Keeps <see cref="SimulationModeIndex"/> in sync when <see cref="SimulationMode"/> changes.</summary>
    partial void OnSimulationModeChanged(CAP.Avalonia.ViewModels.Analysis.SimulationMode value)
    {
        OnPropertyChanged(nameof(SimulationModeIndex));

        // Surface Transient mode to the canvas so laser on/off icons stay visible
        // and clickable during the transient/eye workflow (#690) — Transient mode
        // deliberately clears the CW ShowPowerFlow overlay.
        if (_canvas != null)
            _canvas.IsTransientModeActive = value == CAP.Avalonia.ViewModels.Analysis.SimulationMode.Transient;
    }

    public Commands.CommandManager CommandManager { get; }
    public SimulationService Simulation { get; }

    /// <summary>
    /// ViewModel for canvas interaction (selection, placement, connections).
    /// </summary>
    public CanvasInteractionViewModel CanvasInteraction { get; }

    /// <summary>
    /// ViewModel for file operations (save, load, export).
    /// </summary>
    public FileOperationsViewModel FileOperations { get; }

    /// <summary>
    /// ViewModel for the Home screen (recent projects, new/open project)
    /// shown as the main window's startup state.
    /// </summary>
    public HomeViewModel Home { get; }

    /// <summary>
    /// Design file passed on the command line, resolved by
    /// <see cref="Services.DesignFileArguments.FindDesignFile"/> in App startup.
    /// Consumed once by the main window's Loaded handler; takes precedence
    /// over the reopen-last-project preference. Null when no file was passed.
    /// </summary>
    public string? StartupDesignFile { get; set; }

    /// <summary>
    /// ViewModel for viewport control (zoom, pan, navigation).
    /// </summary>
    public ViewportControlViewModel ViewportControl { get; }

    /// <summary>
    /// ViewModel for the mode-slice probe flyout (issue #691); null when the mode-solver
    /// feature is not registered (e.g. lightweight test construction).
    /// </summary>
    public ViewModels.Solvers.ModeProbe.ModeProbeViewModel? ModeProbe { get; }

    /// <summary>
    /// ViewModel for the left sidebar panel (component library, PDK management).
    /// </summary>
    public LeftPanelViewModel LeftPanel { get; }

    /// <summary>
    /// ViewModel behind the library panel's "Import GDS" button (issue #808):
    /// picks the .gds file and opens the import dialog. Deliberately NOT part of
    /// <see cref="LeftPanelViewModel"/> — GDS import is a self-contained flow that
    /// only reads the panel's template list and registration callback.
    /// </summary>
    public ViewModels.GdsImport.GdsImportButtonViewModel GdsImport { get; }

    /// <summary>
    /// ViewModel for the right sidebar panel (analysis, diagnostics, validation).
    /// </summary>
    public RightPanelViewModel RightPanel { get; }

    /// <summary>
    /// ViewModel for the bottom panel (waveguide length, element locking, status).
    /// </summary>
    public BottomPanelViewModel BottomPanel { get; }

    /// <summary>
    /// Browser for the open photonic component registry (issue #656), hosted in
    /// its own "Component Registry" tool window (opened from the Component
    /// Library header and the Tools flyout; see <c>MainWindow</c>).
    /// </summary>
    public ViewModels.ComponentRegistry.RegistryBrowser.RegistryBrowserViewModel Registry { get; }

    /// <summary>
    /// ViewModel for software update checking. Shared with the Settings window.
    /// The update banner in the main window binds to this property.
    /// </summary>
    public UpdateViewModel Update { get; }

    /// <summary>
    /// Delegate wired by <see cref="CAP.Avalonia.Views.MainWindow"/> to open
    /// the Settings window. The optional page-type argument asks the window
    /// to pre-select a specific <c>ISettingsPage</c> by runtime type (used by
    /// shortcut buttons like "Set API key in Settings" in the AI panel);
    /// pass <c>null</c> for default behavior.
    /// </summary>
    public Func<Type?, Task>? ShowSettingsWindowAsync { get; set; }

    /// <summary>
    /// ViewModel for the unified Export menu flyout.
    /// Holds all registered <see cref="IExportFormat"/> implementations.
    /// </summary>
    public ExportMenuViewModel ExportMenu { get; }

    /// <summary>PhotonTorch format — exposes <c>ShowOptionsDialogAsync</c> for code-behind wiring.</summary>
    public PhotonTorchExportFormat PhotonTorchExportFormat { get; private set; } = null!;

    /// <summary>gdsfactory format — exposes <c>ShowOptionsDialogAsync</c> for code-behind wiring.</summary>
    public GdsFactoryExportFormat GdsFactoryExportFormat { get; private set; } = null!;

    /// <summary>gdsfactory export options/executor ViewModel (dialog DataContext).</summary>
    public ViewModels.Export.GdsFactoryExportViewModel GdsFactoryExport { get; private set; } = null!;

    /// <summary>Verilog-A format — exposes <c>ShowOptionsDialogAsync</c> for code-behind wiring.</summary>
    public VerilogAExportFormat VerilogAExportFormat { get; private set; } = null!;

    public IFileDialogService? FileDialogService
    {
        get => FileOperations.FileDialogService;
        set
        {
            FileOperations.FileDialogService = value;
            FileOperations.PhotonTorchExport.FileDialogService = value;
            FileOperations.VerilogAExport.FileDialogService = value;
            GdsFactoryExport.FileDialogService = value;
            LeftPanel.FileDialogService = value;
            GdsImport.FileDialogService = value;
        }
    }

    private readonly Services.IUrlLauncher _urlLauncher;

    private bool _isSimulating;

    /// <summary>
    /// ViewModel for the PDK Component Offset Editor window.
    /// Exposed so the code-behind can pass the FileDialogService.
    /// </summary>
    public PdkOffsetEditorViewModel PdkOffsetEditor { get; }

    /// <summary>
    /// Service that fetches and caches GDS polygon previews for canvas components.
    /// Exposed so <see cref="CAP.Avalonia.Controls.DesignCanvas"/> can wire up a
    /// repaint callback and pass the service into the render context.
    /// </summary>
    public GdsPreviewRenderService GdsPreviewRenderService { get; }

    /// <summary>
    /// Adaptive crossing-insertion wiring (Issue #553). Held so the binder —
    /// which attaches the crossing-insertion service to the canvas — lives for
    /// the application lifetime. Null in tests that bypass DI.
    /// </summary>
    public ViewModels.Canvas.CrossingInsertion.CrossingInsertionCanvasBinder? CrossingInsertionBinder { get; }

    /// <summary>
    /// Bottom-panel error console service. Exposed so view-layer wiring helpers
    /// (e.g. <see cref="CAP.Avalonia.Views.Dialogs.ExportDialogWiring"/>) can persist
    /// failures that would otherwise only flash through the ephemeral status bar.
    /// </summary>
    public ErrorConsoleService ErrorConsole { get; }

    /// <summary>
    /// Chip-size ViewModel. Singleton — same instance is bound by the Settings window
    /// page and consulted here for save/load and design-checks bounds.
    /// </summary>
    public ViewModels.Canvas.ChipSizeViewModel ChipSize { get; }

    /// <summary>
    /// Per-layer visibility of imported GDS geometry (issue #858). The canvas
    /// render context consults its <c>State</c>; the Imported Layers panel binds
    /// to its rows; settings are persisted per design in the .lun file.
    /// </summary>
    public ViewModels.GdsImport.LayerVisibility.GdsLayerVisibilityViewModel LayerVisibility { get; }

    public MainViewModel(
        DesignCanvasViewModel canvas,
        SimulationService simulationService,
        SimpleNazcaExporter nazcaExporter,
        SaxExporter saxExporter,
        Commands.CommandManager commandManager,
        UserPreferencesService preferencesService,
        Services.GroupPreviewGenerator previewGenerator,
        Services.IInputDialogService inputDialogService,
        ErrorConsoleService errorConsoleService,
        GdsExportViewModel gdsExportViewModel,
        UpdateViewModel updateViewModel,
        LeftPanelViewModel leftPanel,
        RightPanelViewModel rightPanel,
        BottomPanelViewModel bottomPanel,
        ViewportControlViewModel viewportControl,
        PdkOffsetEditorViewModel pdkOffsetEditor,
        ViewModels.Export.PhotonTorchExportViewModel photonTorchExport,
        ViewModels.Export.VerilogAExportViewModel verilogAExport,
        ViewModels.Export.GdsFactoryExportViewModel gdsFactoryExport,
        ViewModels.Canvas.ChipSizeViewModel chipSize,
        Services.UserSMatrixOverrideStore userSMatrixOverrideStore,
        GdsPreviewRenderService gdsPreviewRenderService,
        ViewModels.ComponentRegistry.RegistryBrowser.RegistryBrowserViewModel registryBrowser,
        ViewModels.GdsImport.GdsImportButtonViewModel gdsImportButton,
        Services.IUrlLauncher? urlLauncher = null,
        Services.IAiGridService? aiGridService = null,
        Services.RecentProjectsService? recentProjectsService = null,
        HomeViewModel? homeViewModel = null,
        ViewModels.Canvas.CrossingInsertion.CrossingInsertionCanvasBinder? crossingInsertionBinder = null,
        ViewModels.Solvers.ModeProbe.ModeProbeViewModel? modeProbe = null,
        Services.GdsImport.DesignScope.DesignScopedGdsComponentService? designScopedGdsComponents = null,
        ViewModels.GdsImport.LayerVisibility.GdsLayerVisibilityViewModel? layerVisibility = null)
    {
        _urlLauncher = urlLauncher ?? Services.PlatformShellLauncher.CreateDefault();
        // Injected for activation: constructing the binder wires the adaptive
        // crossing-insertion service (Issue #553) into the canvas' connection manager.
        CrossingInsertionBinder = crossingInsertionBinder;
        Simulation = simulationService;
        CommandManager = commandManager;
        _canvas = canvas;
        PdkOffsetEditor = pdkOffsetEditor;
        GdsPreviewRenderService = gdsPreviewRenderService;
        ErrorConsole = errorConsoleService;
        ChipSize = chipSize;
        _canvas.SimulationRequested = async () => await ExecuteSimulation();
        Update = updateViewModel;

        // Wire panel ViewModels (injected via DI)
        LeftPanel = leftPanel;
        RightPanel = rightPanel;
        BottomPanel = bottomPanel;
        Registry = registryBrowser;

        GdsImport = gdsImportButton;

        CanvasInteraction = new CanvasInteractionViewModel(_canvas, commandManager, LeftPanel.ComponentLibrary, previewGenerator, inputDialogService, errorConsoleService);

        var recentProjects = recentProjectsService ?? new Services.RecentProjectsService(preferencesService);
        FileOperations = new FileOperationsViewModel(_canvas, commandManager, nazcaExporter, saxExporter, LeftPanel.AllTemplates, gdsExportViewModel, photonTorchExport, verilogAExport, errorConsoleService, userSMatrixOverrideStore, recentProjects: recentProjects);
        // Design-scoped GDS imports (#830): save embeds them in the .lun, load
        // restores/migrates them, New Project clears them.
        FileOperations.DesignScopedGdsComponents = designScopedGdsComponents;
        // Per-layer visibility of imported GDS geometry (#858): edits mark the
        // design dirty, and save/load/new-project round-trip through the .lun.
        LayerVisibility = layerVisibility ?? new ViewModels.GdsImport.LayerVisibility.GdsLayerVisibilityViewModel(_canvas);
        LayerVisibility.SettingsEdited = () => FileOperations.HasUnsavedChanges = true;
        FileOperations.LayerVisibility = LayerVisibility;
        ViewportControl = viewportControl;

        // Home screen: shown at startup; delegates project I/O to FileOperations
        // and dismisses itself once a project is opened or created.
        Home = homeViewModel ?? new HomeViewModel(recentProjects, preferencesService);
        Home.NewProjectRequested = async () => await FileOperations.NewProjectCommand.ExecuteAsync(null);
        Home.OpenProjectRequested = async () => await FileOperations.LoadDesignCommand.ExecuteAsync(null);
        Home.OpenProjectFromPathRequested = FileOperations.LoadDesignFromPathAsync;
        Home.OpenExampleRequested = FileOperations.OpenDesignAsCopyAsync;
        FileOperations.ProjectOpened = Home.OnProjectOpened;

        // Keep the window title in sync with the open file and dirty state
        FileOperations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileOperationsViewModel.CurrentFilePath)
                or nameof(FileOperationsViewModel.HasUnsavedChanges))
            {
                UpdateWindowTitle();
            }
        };

        // Build the unified Export menu (add new IExportFormat here for new formats)
        PhotonTorchExportFormat = new PhotonTorchExportFormat();
        VerilogAExportFormat = new VerilogAExportFormat(verilogAExport);
        GdsFactoryExportFormat = new GdsFactoryExportFormat();
        GdsFactoryExport = gdsFactoryExport;
        // By-value member PDKs for the active process (issue placement-livemembers, #732): a
        // custom PDK registered after the process was saved is missing from its persisted
        // MemberPdkNames snapshot but may still be the same process by value — this recomputes
        // the allowed set live against the current catalog, same as the library-filter lock.
        // Shared by the placement guards below AND the metal-spec providers, so placement and
        // export agree on membership.
        Func<IReadOnlyCollection<string>?> getLiveMemberPdkNames = () =>
            FileOperations.ActiveProcess is { } activeProcess ? LeftPanel.ResolveLiveMemberPdkNames(activeProcess) : null;

        // Electrical metal routing spec (#682): trace width / layers / crossing policy come
        // from the active process's metal cross-section; both exporters share one provider.
        // The live member set replaces the stale snapshot so a live-allowed custom PDK's metal
        // xsection / bridge policy reaches the export (review Finding 0).
        Func<CAP_Core.Routing.MetalRouting.MetalRoutingSpec> metalSpecProvider = () =>
            CAP_DataAccess.Components.ComponentDraftMapper.MetalRoutingSpecFactory.FromActiveProcess(
                FileOperations.ActiveProcess, LeftPanel.GetLoadedPdkDrafts(), getLiveMemberPdkNames());
        FileOperations.MetalRoutingSpecProvider = metalSpecProvider;
        GdsFactoryExport.MetalRoutingSpecProvider = metalSpecProvider;
        // Minimum waveguide bend radius (#574): an in-canvas bend-handle drag (and its undo/redo
        // command) must not shrink a bend below what the active process allows. Same
        // active-process + live-member lookup as the metal spec; falls back to the absolute
        // minimum when no process is resolvable (playground / no declared optical minimum).
        Func<double> resolveMinBendRadiusMicrometers = () =>
            CAP_DataAccess.Components.ComponentDraftMapper.WaveguideBendRadiusResolver.Resolve(
                FileOperations.ActiveProcess, LeftPanel.GetLoadedPdkDrafts(), getLiveMemberPdkNames());
        CanvasInteraction.GetMinBendRadiusMicrometers = resolveMinBendRadiusMicrometers;
        // The same process minimum floors the automatic routing and the styled curves:
        // the orchestrator refreshes the router before every routing pass, so AUTO cannot
        // bend tighter than the active process allows.
        _canvas.Routing.GetProcessMinBendRadiusMicrometers = resolveMinBendRadiusMicrometers;
        // Let a Nazca export that hits gdsfactory-native components hand off to the gdsfactory export.
        FileOperations.RequestGdsFactoryExport = () => GdsFactoryExport.Export();
        ExportMenu = new ExportMenuViewModel(new IExportFormat[]
        {
            new NazcaExportFormat(FileOperations.ExportNazcaCommand),
            GdsFactoryExportFormat,
            new SaxExportFormat(FileOperations.ExportSaxCommand),
            PhotonTorchExportFormat,
            VerilogAExportFormat,
            // Circuit-topology netlist (gdsfactory YAML, #687) — same save flow as the panel.
            new NetlistExportFormat(RightPanel.Netlist.SaveYamlCommand),
        });

        // Wire up status callbacks
        CanvasInteraction.UpdateStatus = UpdateStatusText;
        FileOperations.UpdateStatus = UpdateStatusText;
        ViewportControl.UpdateStatus = UpdateStatusText;
        LeftPanel.UpdateStatus = UpdateStatusText;
        GdsImport.UpdateStatus = UpdateStatusText;
        // A .gds/.gdsii pick in the File→Open dialog routes into the GDS import
        // flow (FileOperations owns no import knowledge beyond the callback).
        FileOperations.OpenGdsImportRequested = GdsImport.OpenGdsImportDialogForFileAsync;
        // Key-preserving sink: lets a live UI language switch re-translate the startup
        // "Loaded N component types" status while it is still showing.
        LeftPanel.UpdateLocalizedStatus = SetLocalizedStatus;

        // A saved component definition takes effect type-wide: push the new PDK S-matrices
        // into already-placed instances; explicit overrides keep winning.
        LeftPanel.TemplateDefinitionSaved = FileOperations.RefreshInstancesFromTemplate;

        // Offset-editor fork-on-save: saving a bundled PDK writes the user's copy into
        // user-pdks; the library must swap to (shadow with) that fork, same as the
        // component editor's fork flow.
        PdkOffsetEditor.BundledPdkForkSaved = LeftPanel.RegisterSavedPdkFork;
        // Direct (non-fork) offset-editor saves target the retargeted fork file or a
        // registered custom PDK — refresh the library's in-memory templates so exports
        // and new placements pick up the saved values without a restart.
        PdkOffsetEditor.UserPdkSaved = LeftPanel.RefreshRegisteredPdkAfterExternalSave;

        // Single-process enforcement (issues #570/#653/#737): every placement surface —
        // manual placement/paste, saved group templates, and the AI assistant — shares ONE
        // policy context, so the active process, the process-agnostic tool PDKs, and the
        // library-based PDK-source resolver (groups/pasted copies carry no source of their
        // own, so children are resolved against the loaded library) can never diverge.
        var placementContext = new PlacementPolicyContext(
            getActiveProcess: () => FileOperations.ActiveProcess,
            getProcessAgnosticPdkNames: () => LeftPanel.GetProcessAgnosticPdkNames(),
            resolveComponentPdkSource: component =>
                ViewModels.Library.ComponentPdkSourceResolver.Resolve(component, LeftPanel.AllTemplates),
            resolveLiveMemberPdkNames: () =>
                FileOperations.ActiveProcess is { } activeProcess
                    ? LeftPanel.ResolveLiveMemberPdkNames(activeProcess)
                    : null);

        CanvasInteraction.PlacementContext = placementContext;
        _canvas.Clipboard.PdkSourceResolver = placementContext.ResolveComponentPdkSource;

        if (aiGridService is Services.AiGridService aiGrid)
        {
            aiGrid.PlacementContext = placementContext;
        }

        // Feed the design's active process (#570) into the registry browser so
        // components from a different fabrication process are flagged (#656).
        // Playground accepts everything, so it clears the filter.
        Registry.ActiveProcessId = RegistryProcessIdFor(FileOperations.ActiveProcess);
        FileOperations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileOperationsViewModel.ActiveProcess))
                Registry.ActiveProcessId = RegistryProcessIdFor(FileOperations.ActiveProcess);
        };

        // Let the export guard open the Settings window (e.g. on the Python-Environments
        // page when Nazca is missing); ShowSettingsWindowAsync is wired later by MainWindow.
        FileOperations.ShowSettingsWindow = async pageType =>
        {
            if (ShowSettingsWindowAsync != null)
                await ShowSettingsWindowAsync(pageType);
        };

        // Wire up canvas status updates to bottom panel
        _canvas.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DesignCanvasViewModel.RoutingStatusText))
            {
                var routingText = _canvas.RoutingStatusText;
                if (!string.IsNullOrEmpty(routingText))
                {
                    UpdateStatusText(routingText);
                }
            }
        };

        // Analysis-output picker (#754): the dock header button and both analysis tabs
        // can switch the canvas into the eyedropper picker mode.
        Action activateOutputPicker = () => CanvasInteraction.SetPickAnalysisOutputModeCommand.Execute(null);
        BottomPanel.Analysis.Output.PickRequested = activateOutputPicker;
        BottomPanel.Analysis.Eye.RequestOutputPicker = activateOutputPicker;
        BottomPanel.Analysis.Transient.RequestOutputPicker = activateOutputPicker;

        // Monte-Carlo fabrication variance: the sigma inputs pre-fill from the
        // tolerances declared by the active PDK process.
        BottomPanel.Analysis.MonteCarlo.ActiveProcessProvider = () => FileOperations.ActiveProcess;
        BottomPanel.Analysis.MonteCarlo.RefreshTolerancesFromProcess();
        FileOperations.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(FileOperationsViewModel.ActiveProcess))
                BottomPanel.Analysis.MonteCarlo.RefreshTolerancesFromProcess();
        };

        // Wire up callbacks
        CanvasInteraction.OnSelectionChanged = comp =>
        {
            RightPanel.Sweep.ConfigureForComponent(comp, Canvas);
            BottomPanel.Analysis.Optimization.RefreshFromCanvas();
            LeftPanel.HierarchyPanel.SyncSelectionFromCanvas(comp);
        };

        // Mode-slice probe (issue #691): clicking an element in Probe mode opens the
        // non-modal flyout at the click point, auto-filled from PDK/connection data.
        ModeProbe = modeProbe;
        if (ModeProbe != null)
        {
            ModeProbe.GetActiveProcessFingerprint = () => FileOperations.ActiveProcess?.Fingerprint;
            ModeProbe.GetSimulationWavelengthNm = () =>
                Canvas.Components.FirstOrDefault(c => c.IsLightSource)?.LaserConfig?.WavelengthNm;
            CanvasInteraction.ProbeRequested = (target, canvasX, canvasY) =>
            {
                // Canvas → control pixels, so the flyout opens where the user clicked.
                var zoom = ViewportControl.ZoomLevel;
                ModeProbe.Open(target, canvasX * zoom + Canvas.PanX, canvasY * zoom + Canvas.PanY);
            };
        }

        // Wire rename from hierarchy panel through undo-aware command manager
        LeftPanel.HierarchyPanel.RenameComponent = (component, newName) =>
        {
            var cmd = new Commands.RenameComponentCommand(component, newName);
            CommandManager.ExecuteCommand(cmd);
            LeftPanel.HierarchyPanel.RefreshNode(component);
        };

        CanvasInteraction.ClearLeftPanelGroupSelection = () =>
        {
            LeftPanel.SelectedGroupTemplate = null;
        };

        CanvasInteraction.ClearComponentTemplateSelection = () =>
        {
            CanvasInteraction.SelectedTemplate = null;
        };

        // Wire up mode changes and template selection to keep UI in sync
        CanvasInteraction.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(CanvasInteraction.CurrentMode))
            {
                var mode = CanvasInteraction.CurrentMode;
                // Deselect templates when switching away from placement modes
                if (mode != InteractionMode.PlaceComponent && mode != InteractionMode.PlaceGroupTemplate)
                {
                    LeftPanel.SelectedGroupTemplate = null;
                    // Note: SelectedTemplate is automatically cleared via CanvasInteraction.OnCurrentModeChanged
                }
            }
            else if (e.PropertyName == nameof(CanvasInteraction.SelectedTemplate))
            {
                // When a component template is selected, deselect group template in left panel
                if (CanvasInteraction.SelectedTemplate != null)
                {
                    LeftPanel.SelectedGroupTemplate = null;
                }
            }
            else if (e.PropertyName == nameof(CanvasInteraction.SelectedGroupTemplate))
            {
                // When a group template is selected, deselect component template
                // (SelectedTemplate is bound to MainViewModel.SelectedTemplate which wraps CanvasInteraction.SelectedTemplate,
                // so it will automatically update the UI ListBox)
            }
            else if (e.PropertyName == nameof(CanvasInteraction.SelectedWaveguideConnection))
            {
                // Feed the selected connection into the routing options panel (issue #574).
                BottomPanel.ConnectionRouting.SelectedConnection =
                    CanvasInteraction.SelectedWaveguideConnection;
            }
        };

        // Wire up group template selection from left panel to canvas interaction
        LeftPanel.OnGroupTemplateSelected = template =>
        {
            // Ensure TemplateGroup is loaded before setting as selected
            if (template.TemplateGroup == null && !string.IsNullOrEmpty(template.FilePath))
            {
                // Try to load the template group data from disk
                try
                {
                    if (System.IO.File.Exists(template.FilePath))
                    {
                        var json = System.IO.File.ReadAllText(template.FilePath);
                        var fileData = System.Text.Json.JsonSerializer.Deserialize<CAP_Core.Components.Creation.GroupLibraryFileData>(json);

                        if (fileData != null && !string.IsNullOrWhiteSpace(fileData.GroupData))
                        {
                            var group = CAP_Core.Components.Creation.GroupTemplateSerializer.Deserialize(fileData.GroupData);
                            if (group != null)
                            {
                                template.TemplateGroup = group;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    StatusText = string.Format(LocalizationService.Instance.Translate("Status.TemplateLoadFailed"), template.Name, ex.Message);
                    BottomPanel.ErrorConsole.Log($"Failed to load template '{template.Name}': {ex.Message}", CAP_Contracts.Logger.LogLevel.Error, ex);
                    return;
                }

                if (template.TemplateGroup == null)
                {
                    StatusText = string.Format(LocalizationService.Instance.Translate("Status.TemplateCorrupted"), template.Name);
                    return;
                }
            }
            CanvasInteraction.SelectedGroupTemplate = template;
        };

        WireDesignValidation();
        WireHierarchyPanel();
        WireFileOperations();
        WireCommandManager();

        // Initialize panels
        LeftPanel.Initialize();
        RightPanel.Initialize();

        // Trigger startup update check after a brief delay to avoid blocking startup
        _ = TriggerStartupUpdateCheckAsync();
    }

    /// <summary>
    /// Waits briefly for the UI to finish loading, then checks for updates in the background.
    /// </summary>
    private async Task TriggerStartupUpdateCheckAsync()
    {
        await Task.Delay(2000);
        await Update.CheckForUpdatesOnStartupAsync();
    }

    private void WireCommandManager()
    {
        // Wire CommandManager to notify RelayCommands when CanUndo/CanRedo changes
        CommandManager.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Commands.CommandManager.CanUndo))
            {
                UndoCommand.NotifyCanExecuteChanged();
            }
            else if (e.PropertyName == nameof(Commands.CommandManager.CanRedo))
            {
                RedoCommand.NotifyCanExecuteChanged();
            }
        };
    }

    private void UpdateStatusText(string text)
    {
        StatusText = text;
        BottomPanel.SetStatus(text);
    }

    /// <summary>
    /// Maps the design's active process to the id the registry browser compares
    /// component processes against. Playground and "no process yet" return null,
    /// which disables mismatch flagging.
    /// </summary>
    private static string? RegistryProcessIdFor(ActiveProcessSelection? selection) =>
        selection == null || selection.IsPlayground ? null : selection.DisplayName;

    private void WireHierarchyPanel()
    {
        LeftPanel.HierarchyPanel.NavigateToPosition = ViewportControl.NavigateCanvasTo;
        LeftPanel.HierarchyPanel.GetViewportSize = ViewportControl.GetViewportSize;
        // OpenComponentSettings is wired by MainWindow.axaml.cs (view layer) so it can open the dialog window.
    }

    private void WireDesignValidation()
    {
        RightPanel.DesignValidation.NavigateToPosition = ViewportControl.NavigateCanvasTo;
        RightPanel.DesignValidation.HighlightConnection = (connection) =>
        {
            foreach (var conn in Canvas.Connections)
            {
                conn.IsSelected = conn.Connection == connection;
            }
        };
    }

    private void WireFileOperations()
    {
        FileOperations.PhotonTorchExport.UpdateStatus = UpdateStatusText;
        FileOperations.RebuildHierarchy = LeftPanel.HierarchyPanel.RebuildTree;

        // Single-process wiring (issue #570): supply the live PDK catalog for the
        // New-Design picker and legacy-file migration, route migration warnings to
        // the status bar, and keep the toolbar indicator in sync with the active process.
        FileOperations.ProcessCatalogProvider = BuildProcessCatalog;
        FileOperations.ProcessAgnosticPdkNamesProvider = () => LeftPanel.GetProcessAgnosticPdkNames();
        // Migration/revalidation warnings must survive the next status update — the
        // status bar is overwritten by the load-complete message an instant later.
        FileOperations.OnProcessMigrationWarning = warning =>
        {
            UpdateStatusText(warning);
            ErrorConsole.LogWarning(warning);
        };
        FileOperations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileOperations.ActiveProcess)) RefreshProcessIndicator();
        };

        // Re-read the VM-side one-time translations when the UI language switches (field bug
        // round 5). Filtered to ActiveLanguageCode because SetLanguage raises several
        // notifications ("Item"/"Item[]" for the AXAML indexer bindings) per switch.
        LocalizationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.ActiveLanguageCode))
                OnUiLanguageChanged();
        };

        // One zoom-to-fit body for both post-content triggers — design load and
        // GDS import placement: prefer the live viewport size over the caller's
        // fallback, then fit the WHOLE canvas content (not just the new part).
        Action<double, double> zoomToFitToViewport = (w, h) =>
        {
            var (vpWidth, vpHeight) = ViewportControl.GetViewportSize?.Invoke() ?? (w, h);
            ViewportControl.ZoomToFit(vpWidth, vpHeight);
        };
        FileOperations.ZoomToFitAfterLoad = zoomToFitToViewport;
        GdsImport.ZoomToFitAfterImport = zoomToFitToViewport;
        // An import that outgrew the chip already resized the canvas; this syncs
        // the chip-size settings panel (same pattern as ApplyChipSizeAfterLoad).
        GdsImport.ApplyChipSizeAfterImport = ChipSize.ApplyFromMicrometers;

        // Restore chip size from saved file without overwriting the user preference default
        FileOperations.ApplyChipSizeAfterLoad = (widthUm, heightUm) =>
            ChipSize.ApplyFromMicrometers(widthUm, heightUm);

        // Auto-check Python/Nazca environment on startup
        // If no custom path is set, trigger auto-discovery
        var gdsExport = FileOperations.GdsExport;
        if (string.IsNullOrEmpty(gdsExport.CustomPythonPath))
        {
            _ = gdsExport.SearchForPythonAsync();
        }
        else
        {
            _ = gdsExport.CheckEnvironmentAsync();
        }
    }

    /// <summary>
    /// Updates <see cref="ActiveProcessLabel"/> and <see cref="IsPlayground"/> from
    /// <see cref="FileOperationsViewModel.ActiveProcess"/> (issue #570). Invoked whenever
    /// the active process changes (New-Design selection, load, or migration).
    /// </summary>
    private void RefreshProcessIndicator()
    {
        var p = FileOperations.ActiveProcess;
        UpdateActiveProcessLabel(p);
        LeftPanel.ApplyActiveProcess(p);
    }

    /// <summary>
    /// Re-reads the VM-side one-time translations after a live language switch: the
    /// active-process badge (recomputed, PDK process lock untouched) and — only while the
    /// status bar shows the idle "Ready" text — the status bar itself.
    /// </summary>
    private void OnUiLanguageChanged()
    {
        UpdateActiveProcessLabel(FileOperations.ActiveProcess);
        if (_lastLocalizedStatus is { } status && StatusText == status.Formatted)
            SetLocalizedStatus(status.Key, status.Args);
    }

    /// <summary>
    /// Sets the status bar from a string-table key (formatted invariantly with
    /// <paramref name="args"/>) and remembers the key, so a live UI language switch can
    /// re-translate the message while it is still showing.
    /// </summary>
    internal void SetLocalizedStatus(string key, params object[] args)
    {
        var formatted = string.Format(
            CultureInfo.InvariantCulture, LocalizationService.Instance.Translate(key), args);
        _lastLocalizedStatus = (key, args, formatted);
        StatusText = formatted;
    }

    /// <summary>
    /// Recomputes the localized <see cref="ActiveProcessLabel"/> and <see cref="IsPlayground"/>
    /// flag and mirrors the label into the canvas HUD — without re-applying the PDK process
    /// lock. Called on process change and on UI language switch so the badge re-reads live.
    /// </summary>
    private void UpdateActiveProcessLabel(CAP_Core.Components.Process.ActiveProcessSelection? p)
    {
        var loc = LocalizationService.Instance;
        IsPlayground = p?.IsPlayground == true;
        ActiveProcessLabel = p == null ? loc.Translate("Process.NoneSelected")
            : p.IsPlayground ? loc.Translate("Process.PlaygroundBadge")
            : string.Format(CultureInfo.InvariantCulture, loc.Translate("Process.Prefix"), p.DisplayName);
        // Mirror into the canvas VM so the status HUD (CanvasOverlayRenderer) can show the
        // active process in the grid overlay, not only at the bottom of the PDK panel.
        Canvas.ActiveProcessLabel = ActiveProcessLabel;
    }

    // Canvas interaction delegates
    public void CanvasClicked(double x, double y) => CanvasInteraction.CanvasClicked(x, y);
    public void PinClicked(PhysicalPin pin) => CanvasInteraction.PinClicked(pin);
    public void CanvasMouseMove(double x, double y) => CanvasInteraction.CanvasMouseMove(x, y);
    public void StartMoveComponent(ComponentViewModel component) => CanvasInteraction.StartMoveComponent(component);
    public void EndMoveComponent() => CanvasInteraction.EndMoveComponent();
    public void StartGroupMove(IEnumerable<ComponentViewModel> components) => CanvasInteraction.StartGroupMove(components);
    public void EndGroupMove(IEnumerable<ComponentViewModel> components) => CanvasInteraction.EndGroupMove(components);
    public void PasteSelected(double? targetX = null, double? targetY = null) => CanvasInteraction.PasteSelected(targetX, targetY);

    // Viewport control delegates
    public void ZoomToFit(double viewportWidth, double viewportHeight) => ViewportControl.ZoomToFit(viewportWidth, viewportHeight);

    // Backward-compatible command delegates
    [RelayCommand]
    private void SetSelectMode() => CanvasInteraction.SetSelectModeCommand.Execute(null);

    [RelayCommand]
    private void SetConnectMode() => CanvasInteraction.SetConnectModeCommand.Execute(null);

    [RelayCommand]
    private void SetDeleteMode() => CanvasInteraction.SetDeleteModeCommand.Execute(null);

    [RelayCommand]
    private void SetProbeMode() => CanvasInteraction.SetProbeModeCommand.Execute(null);

    [RelayCommand]
    private void SetCutMode() => CanvasInteraction.SetCutModeCommand.Execute(null);

    [RelayCommand]
    private void DeleteSelected() => CanvasInteraction.DeleteSelectedCommand.Execute(null);

    [RelayCommand]
    private void CopySelected() => CanvasInteraction.CopySelectedCommand.Execute(null);

    [RelayCommand]
    private void PasteSelectedCommand() => CanvasInteraction.PasteSelectedCommandCommand.Execute(null);

    [RelayCommand]
    private void RotateSelected() => CanvasInteraction.RotateSelectedCommand.Execute(null);

    [RelayCommand]
    private void CreateGroup() => CanvasInteraction.CreateGroupCommand.Execute(null);

    [RelayCommand]
    private void Ungroup() => CanvasInteraction.UngroupCommand.Execute(null);

    [RelayCommand]
    private void ZoomIn() => ViewportControl.ZoomInCommand.Execute(null);

    [RelayCommand]
    private void ZoomOut() => ViewportControl.ZoomOutCommand.Execute(null);

    [RelayCommand]
    private void ResetZoom() => ViewportControl.ResetZoomCommand.Execute(null);

    [RelayCommand]
    private void ResetPan() => ViewportControl.ResetPanCommand.Execute(null);

    [RelayCommand]
    private async Task SaveDesign() => await FileOperations.SaveDesignCommand.ExecuteAsync(null);

    /// <summary>
    /// Recomputes <see cref="WindowTitle"/> from the current file path and dirty state.
    /// </summary>
    private void UpdateWindowTitle()
    {
        var marker = FileOperations.HasUnsavedChanges ? DirtyMarker : "";
        WindowTitle = FileOperations.CurrentFilePath is { } path
            ? $"{System.IO.Path.GetFileName(path)}{marker} — {AppTitle}"
            : FileOperations.HasUnsavedChanges
                ? $"{UntitledProjectName}{marker} — {AppTitle}"
                : AppTitle;
    }

    /// <summary>Shows the Home screen (recent projects, new/open) over the editor.</summary>
    [RelayCommand]
    private void ShowHome() => Home.Show();

    [RelayCommand]
    private async Task SaveDesignAs() => await FileOperations.SaveDesignAsCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task LoadDesign() => await FileOperations.LoadDesignCommand.ExecuteAsync(null);

    /// <summary>
    /// Starts a new design: first clears the canvas via
    /// <see cref="FileOperationsViewModel.NewProjectCommand"/> — which prompts to save
    /// unsaved changes and silently no-ops if the user cancels that prompt — and only
    /// once the canvas is confirmed empty does it ask which fabrication process to lock
    /// the fresh design to (or Playground), issue #570. This ordering is required so a
    /// picked process is never applied to a design that failed to clear (the exact
    /// data-integrity bug the process lock exists to prevent). Cancelling the process
    /// picker after a successful clear simply leaves the new, empty design with no
    /// process set. When no picker is wired (headless/test contexts), the canvas is
    /// cleared and no process is set.
    /// </summary>
    [RelayCommand]
    private async Task NewProject()
    {
        // TryNewProjectAsync reports cancellation explicitly. An empty canvas is NOT a
        // usable success signal: the canvas can already be empty while the user cancels
        // the unsaved-changes prompt, and showing the picker then would overwrite the
        // process of a design the user explicitly chose to keep (issue #570).
        var created = await FileOperations.TryNewProjectAsync();
        if (!created)
            return;

        await PickAndApplyProcessAsync();
    }

    /// <summary>
    /// Prompts once at startup for the fabrication process (the same picker as New Design),
    /// unless a design that already carries a process was loaded first. Invoked by the view
    /// after the window opens (issue #570). Dismissing the picker starts in Playground.
    /// </summary>
    public async Task PromptForInitialProcessAsync()
    {
        if (FileOperations.ActiveProcess != null)
            return;   // a design (with its process) is already established

        await PickAndApplyProcessAsync();
    }

    /// <summary>
    /// Builds the live process catalog from the loaded PDKs. Single definition shared by
    /// the New-Design/startup picker and the file-load migration path, so the two can
    /// never diverge on how the catalog is constructed.
    /// </summary>
    private IReadOnlyList<ProcessGroup> BuildProcessCatalog() =>
        ProcessCatalog.BuildGroups(LeftPanel.GetLoadedPdkProcessEntries());

    /// <summary>
    /// Shows the process picker and applies the result to the design. Dismissing the picker
    /// defaults to Playground (not manufacturable) rather than leaving the process undefined,
    /// so the design is always in a known state (issue #570). No-op when no picker is wired
    /// (headless/tests), leaving the process unset.
    /// </summary>
    private async Task PickAndApplyProcessAsync()
    {
        if (ShowProcessSelectionAsync == null)
            return;

        var groups = BuildProcessCatalog();
        var selection = await ShowProcessSelectionAsync(groups);
        // markDirty: false — picking the baseline process of a fresh, empty design is
        // not an unsaved change; marking it dirty made every launch (and every File→New)
        // answer a spurious "Save changes?" prompt for an untouched design.
        FileOperations.SetActiveProcess(selection ?? ActiveProcessSelection.Playground(), markDirty: false);
    }

    [RelayCommand]
    private async Task ExportNazca() => await FileOperations.ExportNazcaCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task ExportSax() => await FileOperations.ExportSaxCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task LoadPdk() => await LeftPanel.LoadPdkCommand.ExecuteAsync(null);

    /// <summary>
    /// Raised when the user requests to open the PDK Offset Editor window.
    /// The View layer subscribes and shows the window.
    /// </summary>
    public Action? ShowPdkOffsetEditorRequested { get; set; }

    [RelayCommand]
    private void OpenPdkOffsetEditor()
    {
        ShowPdkOffsetEditorRequested?.Invoke();
    }

    /// <summary>
    /// Shows the New-Design process-selection dialog (issue #570) and returns the
    /// user's choice, or null if the user cancelled. Wired by
    /// <see cref="CAP.Avalonia.Views.MainWindow"/>; left null in headless/test
    /// contexts, in which case <see cref="NewProject"/> skips the dialog and
    /// proceeds without locking a process.
    /// </summary>
    public Func<IReadOnlyList<ProcessGroup>, Task<ActiveProcessSelection?>>? ShowProcessSelectionAsync { get; set; }

    [RelayCommand]
    private void OpenPdkHelp()
    {
        var url = "https://github.com/aignermax/Lunima/blob/main/docs/PDK_JSON_FORMAT.md";

        try
        {
            _urlLauncher.Open(url);
            StatusText = LocalizationService.Instance.Translate("Status.OpeningPdkHelp");
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance.Translate("Status.CouldNotOpenBrowser"), ex.Message);
        }
    }

    /// <summary>
    /// Opens the application Settings window. The concrete window creation
    /// is wired by <see cref="CAP.Avalonia.Views.MainWindow"/>.
    /// </summary>
    [RelayCommand]
    private async Task OpenSettingsWindow()
    {
        if (ShowSettingsWindowAsync != null)
            await ShowSettingsWindowAsync(null);
    }

    /// <summary>
    /// Opens the Settings window focused on the AI Assistant page — used by
    /// the right-panel AI shortcut when no API key is configured yet.
    /// </summary>
    [RelayCommand]
    private async Task OpenAiSettings()
    {
        if (ShowSettingsWindowAsync != null)
            await ShowSettingsWindowAsync(typeof(ViewModels.Settings.AiAssistantSettingsPage));
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (CommandManager.Undo())
        {
            StatusText = string.Format(LocalizationService.Instance.Translate("Status.Undone"), CommandManager.RedoDescription ?? "action");
        }
        else
        {
            StatusText = LocalizationService.Instance.Translate("Status.NothingToUndo");
        }
    }

    private bool CanUndo() => CommandManager.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (CommandManager.Redo())
        {
            StatusText = string.Format(LocalizationService.Instance.Translate("Status.Redone"), CommandManager.UndoDescription ?? "action");
        }
        else
        {
            StatusText = LocalizationService.Instance.Translate("Status.NothingToRedo");
        }
    }

    private bool CanRedo() => CommandManager.CanRedo;

    [RelayCommand]
    private async Task RunSimulation()
    {
        if (_isSimulating) return;

        if (SimulationMode == CAP.Avalonia.ViewModels.Analysis.SimulationMode.Transient)
        {
            // Clear any stale CW power-flow overlay so it doesn't render on top of
            // the transient results (matches the CW toggle-off below).
            if (Canvas.ShowPowerFlow)
            {
                Canvas.ShowPowerFlow = false;
                Canvas.PowerFlowVisualizer.IsEnabled = false;
            }

            BottomPanel.Analysis.OpenTransient();
            await BottomPanel.Analysis.Transient.RunTransientCommand.ExecuteAsync(null);
            return;
        }

        // Toggle off if overlay is already showing
        if (Canvas.ShowPowerFlow)
        {
            Canvas.ShowPowerFlow = false;
            Canvas.PowerFlowVisualizer.IsEnabled = false;
            StatusText = LocalizationService.Instance.Translate("Status.SimulationOverlayOff");
            return;
        }

        await ExecuteSimulation();
    }

    /// <summary>
    /// Runs simulation without toggle logic (used by auto-resimulation).
    /// </summary>
    private async Task ExecuteSimulation()
    {
        if (_isSimulating) return;
        _isSimulating = true;

        try
        {
            StatusText = LocalizationService.Instance.Translate("Status.RunningSimulation");
            var result = await Simulation.RunAsync(Canvas);

            if (result.Success)
            {
                StatusText = string.Format(LocalizationService.Instance.Translate("Status.SimulationComplete"),
                             result.LightSourceCount, result.ConnectionCount, result.WavelengthSummary);

                if (result.SystemMatrix != null)
                {
                    RightPanel.SMatrixPerformance.AnalyzeMatrix(result.SystemMatrix);
                }
            }
            else
            {
                StatusText = result.ErrorMessage ?? "Simulation failed";
            }
        }
        catch (CAP_Core.LightCalculation.NonConvergentCircuitException ex)
        {
            // Physics guard (round-4): non-passive data / resonant loop — surface the
            // already-localized guard message instead of a raw exception string.
            var message = Analysis.NonConvergentCircuitMessageFormatter.Format(ex);
            StatusText = message;
            BottomPanel.ErrorConsole.Log($"Simulation blocked: {message}", CAP_Contracts.Logger.LogLevel.Error, ex);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance.Translate("Status.SimulationError"), ex.Message);
            BottomPanel.ErrorConsole.Log($"Simulation failed: {ex.Message}", CAP_Contracts.Logger.LogLevel.Error, ex);

        }
        finally
        {
            _isSimulating = false;
        }
    }

    [RelayCommand]
    private void RunDesignChecks()
    {
        var connections = Canvas.Connections
            .Select(c => c.Connection)
            .ToList();

        var groups = Canvas.Components
            .Select(c => c.Component)
            .OfType<CAP_Core.Components.Core.ComponentGroup>()
            .ToList();

        var allComponents = Canvas.Components
            .Select(c => c.Component)
            .ToList();

        // PDK-process compatibility (issue #570 follow-up, LC-T4): resolve each placed
        // component's PDK source the same way the placement/paste guards do — the snapshot
        // TemplatePdkSource captured when it was placed, falling back to a live library match
        // (see ComponentClipboard/FileOperationsViewModel for the same fallback) — so a process
        // edit that diverges a PDK from the design's active process is flagged for review even
        // though the already-placed components themselves are never touched or deleted.
        var pdkSourceByComponent = Canvas.Components.ToDictionary(
            c => c.Component,
            c => c.TemplatePdkSource ?? CanvasInteraction.PlacementContext.ResolveComponentPdkSource(c.Component));

        // Under a real process lock the allowed set is the lock-derived membership; without one
        // (Playground/no selection) nothing is locked, so GetProcessCompatiblePdkNames() equals
        // all loaded PDK names and only a component whose PDK isn't loaded at all (e.g.
        // trash-deleted while its placed instances were kept, as PdkDelete_Click promises) gets
        // flagged — with a "not loaded" wording instead of a process-mismatch message that would
        // reference a process that doesn't exist (PR #739 review, both directions).
        var processLockActive = FileOperations.ActiveProcess is { IsPlayground: false };
        var compatiblePdkNames = LeftPanel.PdkManager.GetProcessCompatiblePdkNames();

        RightPanel.DesignValidation.RunValidation(
            connections,
            groups,
            allComponents,
            ChipSize.CurrentWidthMicrometers,
            ChipSize.CurrentHeightMicrometers,
            pdkSourceByComponent,
            LeftPanel.GetProcessAgnosticPdkNames(),
            compatiblePdkNames,
            processLockActive);

        StatusText = RightPanel.DesignValidation.StatusText;
    }
}

// Data classes for serialization (used by FileOperationsViewModel)

/// <summary>
/// Root data structure for a .lun design file (Photonic Intermediate Representation).
/// Version 2.0 stores S-matrix data, simulation results, metadata, and external references.
/// Legacy v1 files (FormatVersion missing or different) load with a loud warning in the
/// error console and get upgraded to v2.0 on the next save.
/// </summary>
public class DesignFileData
{
    /// <summary>
    /// File format version. "2.0" is the current format. Other values trigger a loud
    /// warning during load; missing PIR sections remain empty until the next save.
    /// </summary>
    public string? FormatVersion { get; set; }

    public List<ComponentData> Components { get; set; } = new();
    public List<ConnectionData> Connections { get; set; } = new();

    /// <summary>
    /// ComponentGroups with their hierarchical structure, frozen paths, and external pins.
    /// </summary>
    public List<DesignGroupData>? Groups { get; set; }

    /// <summary>
    /// Per-component S-matrix data, keyed by component Identifier string.
    /// Null or empty for designs without stored S-matrix overrides.
    /// </summary>
    public Dictionary<string, ComponentSMatrixData>? SMatrices { get; set; }

    /// <summary>
    /// Most recent simulation results and any stored parameter sweep results.
    /// Null if no simulation has been run and saved.
    /// </summary>
    public SimulationResultsData? SimulationResults { get; set; }

    /// <summary>
    /// Design metadata: PDK versions, design rules, authorship.
    /// Automatically populated with dates on every save.
    /// </summary>
    public DesignMetadata? Metadata { get; set; }

    /// <summary>
    /// References to external simulation or measurement files linked to this design.
    /// Null or empty for designs without external data.
    /// </summary>
    public List<ExternalReferenceData>? ExternalReferences { get; set; }

    /// <summary>
    /// Identifier of the coupler designated as THE analysis output for the Eye/BER
    /// and Transient analyses (#754). Null when no coupler is designated (automatic
    /// selection). Older files without this field load with no designation.
    /// </summary>
    public string? AnalysisOutputCoupler { get; set; }

    /// <summary>
    /// GDS-imported component sets scoped to this design (issue #830): the
    /// component drafts AND the source .gds (base64) travel inside the .lun so
    /// it stays self-contained and portable. Null for designs without GDS
    /// imports; older files reference legacy global import PDKs instead and
    /// are migrated on load.
    /// </summary>
    public List<Services.GdsImport.DesignScope.ImportedGdsComponentSetData>? ImportedGdsComponents { get; set; }

    /// <summary>
    /// Per-layer visibility overrides for imported GDS geometry (issue #858).
    /// Only non-default entries are stored; null when every layer is fully
    /// visible. Older files without this field load with all layers shown.
    /// </summary>
    public List<Services.GdsImport.LayerVisibility.GdsLayerVisibilityData>? LayerVisibility { get; set; }

    /// <summary>
    /// Chip width in micrometers as configured in the Chip Size settings.
    /// Null for files saved before chip-size support was added (defaults to 5000 μm on load).
    /// </summary>
    public double? ChipWidthMicrometers { get; set; }

    /// <summary>
    /// Chip height in micrometers as configured in the Chip Size settings.
    /// Null for files saved before chip-size support was added (defaults to 5000 μm on load).
    /// </summary>
    public double? ChipHeightMicrometers { get; set; }

    /// <summary>
    /// The fabrication process this design is locked to (issue #570 — one process per chip).
    /// Null for legacy files saved before single-process support; migrated on load.
    /// </summary>
    public ActiveProcessData? ActiveProcess { get; set; }
}

/// <summary>
/// DTO for a ComponentGroup in the design file.
/// Bridges the UI-layer (TemplateName-based) and core-layer (ComponentGroupDto) serialization.
/// </summary>
public class DesignGroupData
{
    /// <summary>
    /// Group metadata serialized via ComponentGroupSerializer.
    /// </summary>
    public CAP_DataAccess.Persistence.DTOs.ComponentGroupDto GroupDto { get; set; } = new();

    /// <summary>
    /// Child component data with template names for recreation from the component library.
    /// Maps child Identifier to TemplateName.
    /// </summary>
    public List<ChildComponentData> ChildComponents { get; set; } = new();

    /// <summary>
    /// Canvas X position of the group ViewModel.
    /// </summary>
    public double CanvasX { get; set; }

    /// <summary>
    /// Canvas Y position of the group ViewModel.
    /// </summary>
    public double CanvasY { get; set; }
}

/// <summary>
/// DTO for a child component within a group, preserving template name for library lookup.
/// </summary>
public class ChildComponentData
{
    public string Identifier { get; set; } = "";

    /// <summary>
    /// Guid string of the component instance (stable unique ID).
    /// Used as the primary lookup key during load; falls back to Identifier for old files.
    /// </summary>
    public string? ComponentGuid { get; set; }

    public string TemplateName { get; set; } = "";

    /// <summary>
    /// PDK source name used to disambiguate templates with the same name.
    /// Null in old files — falls back to name-only lookup.
    /// </summary>
    public string? PdkSource { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public int Rotation { get; set; }

    /// <summary>
    /// Exact continuous rotation in degrees (GDS imports keep non-cardinal
    /// angles). Null in old files — falls back to <see cref="Rotation"/>
    /// quarter-turns. Supersedes <see cref="Rotation"/> when present.
    /// </summary>
    public double? RotationDegrees { get; set; }
    public double? SliderValue { get; set; }

    /// <summary>
    /// All slider values keyed by slider number. Supersedes
    /// <see cref="SliderValue"/> for multi-parameter components; null in old files.
    /// </summary>
    public Dictionary<int, double>? SliderValues { get; set; }

    public int? LaserWavelengthNm { get; set; }
    public double? LaserPower { get; set; }
    public bool? IsLocked { get; set; }
    public string? HumanReadableName { get; set; }
}

public class ComponentData
{
    public string TemplateName { get; set; } = "";

    /// <summary>
    /// PDK source name (e.g. "Built-in", "Demo PDK").
    /// Used to disambiguate templates with the same name from different PDKs.
    /// Null in old files — falls back to name-only lookup.
    /// </summary>
    public string? PdkSource { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public string Identifier { get; set; } = "";
    public int Rotation { get; set; }

    /// <summary>
    /// Exact continuous rotation in degrees (GDS imports keep non-cardinal
    /// angles). Null in old files — falls back to <see cref="Rotation"/>
    /// quarter-turns. Supersedes <see cref="Rotation"/> when present.
    /// </summary>
    public double? RotationDegrees { get; set; }
    public double? SliderValue { get; set; }

    /// <summary>
    /// All slider values keyed by slider number. Supersedes
    /// <see cref="SliderValue"/> for multi-parameter components; null in old files.
    /// </summary>
    public Dictionary<int, double>? SliderValues { get; set; }

    public int? LaserWavelengthNm { get; set; }
    public double? LaserPower { get; set; }

    /// <summary>Per-coupler laser on/off (#690). Null in old files — treated as on.</summary>
    public bool? LaserEnabled { get; set; }

    /// <summary>Spectral line shape name (#819). Null in old files — ideal source.</summary>
    public string? LaserLineShape { get; set; }

    /// <summary>Linewidth (FWHM) in nm (#819). Null in old files — editor default.</summary>
    public double? LaserLinewidthFwhmNm { get; set; }

    /// <summary>Relative intensity noise in dB/Hz (#819). Null in old files — default RIN.</summary>
    public double? LaserRinDbPerHz { get; set; }

    public bool? IsLocked { get; set; }

    /// <summary>
    /// True only for crossing components the crossing-insertion pass placed
    /// automatically (#705); null otherwise and in old files. Used after load
    /// to rebuild the crossings' dissolution records.
    /// </summary>
    public bool? IsInsertedCrossing { get; set; }

    public string? HumanReadableName { get; set; }
}

public class ConnectionData
{
    public int StartComponentIndex { get; set; }
    public string StartPinName { get; set; } = "";
    public int EndComponentIndex { get; set; }
    public string EndPinName { get; set; } = "";

    /// <summary>
    /// Stable component identifier for the start endpoint (preferred over StartComponentIndex).
    /// Populated in new saves; null in old files (fall back to StartComponentIndex).
    /// </summary>
    public string? StartComponentId { get; set; }

    /// <summary>
    /// Stable component identifier for the end endpoint (preferred over EndComponentIndex).
    /// Populated in new saves; null in old files (fall back to EndComponentIndex).
    /// </summary>
    public string? EndComponentId { get; set; }

    public List<PathSegmentData>? CachedSegments { get; set; }
    public bool? IsBlockedFallback { get; set; }

    /// <summary>
    /// True when the cached route violates a physical constraint (e.g. bend radius). Null in
    /// old files (predates this field) — treated as false, matching their pre-existing behavior.
    /// </summary>
    public bool? IsInvalidGeometry { get; set; }

    /// <summary>
    /// True when the cached route is an honest placeholder rather than real geometry (the
    /// router replaced a self-crossing fallback with a straight line — see
    /// <see cref="CAP_Core.Routing.RoutedPath.IsPlaceholderGeometry"/>). Written as an
    /// explicit true/false whenever a route was cached (never omitted just because it is
    /// false), so null unambiguously means the file predates this field — <see
    /// cref="CAP.Avalonia.ViewModels.Converters.PathSegmentConverter.ToRoutedPath"/> then
    /// infers it from the route's shape instead of trusting a bare false.
    /// </summary>
    public bool? IsPlaceholderGeometry { get; set; }

    public bool? IsLocked { get; set; }

    /// <summary>Routing style name (WaveguideType); null = Auto (issue #574).</summary>
    public string? RoutingStyle { get; set; }

    /// <summary>Waveguide width in µm; null = model default.</summary>
    public double? WidthMicrometers { get; set; }

    /// <summary>Bend radius in µm; null = model default.</summary>
    public double? BendRadiusMicrometers { get; set; }

    /// <summary>True when the routed path is frozen (manual bend edits); null = false.</summary>
    public bool? IsRouteFrozen { get; set; }

    /// <summary>Manual per-bend radius overrides (bend index → radius µm); null = none.</summary>
    public Dictionary<int, double>? BendRadiusOverrides { get; set; }

    /// <summary>Manual straight-segment shifts (straight index → offset µm, issue #791); null = none.</summary>
    public Dictionary<int, double>? StraightShiftOffsets { get; set; }

    /// <summary>
    /// GDS layer of the import source route polygons (route-derived GDS connections),
    /// paired with <see cref="SourceGdsDataType"/>. Null for app-created connections
    /// and in files that predate the field — the export then uses the process defaults.
    /// </summary>
    public int? SourceGdsLayer { get; set; }

    /// <summary>GDS datatype of the import source route polygons — see <see cref="SourceGdsLayer"/>.</summary>
    public int? SourceGdsDataType { get; set; }
}

/// <summary>
/// DTO for serializing waveguide path segments.
/// </summary>
public class PathSegmentData
{
    public string Type { get; set; } = "";
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public double StartAngleDegrees { get; set; }
    public double EndAngleDegrees { get; set; }
    public double? CenterX { get; set; }
    public double? CenterY { get; set; }
    public double? RadiusMicrometers { get; set; }
    public double? SweepAngleDegrees { get; set; }
}
