using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Components.Core;
using CAP_Core;
using CAP.Avalonia.Commands;
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
    private string _statusText = "Ready";

    /// <summary>
    /// Human-readable label for the design's active fabrication process (issue #570),
    /// e.g. "Process: Generic SOI 220 nm" or "Playground — not manufacturable". Kept in
    /// sync with <see cref="FileOperationsViewModel.ActiveProcess"/> by
    /// <see cref="RefreshProcessIndicator"/>. Bound by the toolbar indicator chip.
    /// </summary>
    [ObservableProperty]
    private string _activeProcessLabel = "No process selected";

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
    /// ViewModel for viewport control (zoom, pan, navigation).
    /// </summary>
    public ViewportControlViewModel ViewportControl { get; }

    /// <summary>
    /// ViewModel for the left sidebar panel (component library, PDK management).
    /// </summary>
    public LeftPanelViewModel LeftPanel { get; }

    /// <summary>
    /// ViewModel for the right sidebar panel (analysis, diagnostics, validation).
    /// </summary>
    public RightPanelViewModel RightPanel { get; }

    /// <summary>
    /// ViewModel for the bottom panel (waveguide length, element locking, status).
    /// </summary>
    public BottomPanelViewModel BottomPanel { get; }

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
        Services.IUrlLauncher? urlLauncher = null,
        Services.IAiGridService? aiGridService = null)
    {
        _urlLauncher = urlLauncher ?? Services.PlatformShellLauncher.CreateDefault();
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

        CanvasInteraction = new CanvasInteractionViewModel(_canvas, commandManager, LeftPanel.ComponentLibrary, previewGenerator, inputDialogService);

        FileOperations = new FileOperationsViewModel(_canvas, commandManager, nazcaExporter, saxExporter, LeftPanel.AllTemplates, gdsExportViewModel, photonTorchExport, verilogAExport, errorConsoleService, userSMatrixOverrideStore);
        ViewportControl = viewportControl;

        // Build the unified Export menu (add new IExportFormat here for new formats)
        PhotonTorchExportFormat = new PhotonTorchExportFormat();
        VerilogAExportFormat = new VerilogAExportFormat(verilogAExport);
        GdsFactoryExportFormat = new GdsFactoryExportFormat();
        GdsFactoryExport = gdsFactoryExport;
        // gdsfactory export honours gdsfactory-backend overrides from the design's store.
        GdsFactoryExport.OverridesProvider = () => FileOperations.StoredNazcaOverrides;
        // Let a Nazca export that hits gdsfactory-native components hand off to the gdsfactory export.
        FileOperations.RequestGdsFactoryExport = () => GdsFactoryExport.Export();
        // Electrical connections export as metal traces (#682); resolve the metal layer/width from
        // the design's active process (its member PDKs' metal cross-section), else a safe default.
        Func<CAP_Core.Export.MetalTraceStyle> resolveMetalStyle = () =>
            CAP_DataAccess.Components.ComponentDraftMapper.MetalTraceStyleResolver.Resolve(
                FileOperations.ActiveProcess, LeftPanel.GetLoadedPdkDrafts());
        GdsFactoryExport.MetalStyleProvider = resolveMetalStyle;
        FileOperations.MetalStyleProvider = resolveMetalStyle;
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

        // Single-process enforcement (issues #570/#653): every placement surface — manual
        // placement/paste, saved group templates, and the AI assistant — consults the
        // same active process, the same process-agnostic tool PDKs, and the same
        // library-based PDK-source resolver (groups/pasted copies carry no source of
        // their own, so children are resolved against the loaded library).
        Func<ActiveProcessSelection?> getActiveProcess = () => FileOperations.ActiveProcess;
        Func<IReadOnlyCollection<string>> getAgnosticPdkNames = () => LeftPanel.GetProcessAgnosticPdkNames();
        Func<CAP_Core.Components.Core.Component, string?> resolvePdkSource = component =>
            ViewModels.Library.ComponentPdkSourceResolver.Resolve(component, LeftPanel.AllTemplates);

        CanvasInteraction.GetActiveProcess = getActiveProcess;
        CanvasInteraction.GetProcessAgnosticPdkNames = getAgnosticPdkNames;
        CanvasInteraction.ResolveComponentPdkSource = resolvePdkSource;
        _canvas.Clipboard.PdkSourceResolver = resolvePdkSource;
        if (aiGridService is Services.AiGridService aiGrid)
        {
            aiGrid.GetActiveProcess = getActiveProcess;
            aiGrid.GetProcessAgnosticPdkNames = getAgnosticPdkNames;
            aiGrid.ResolveComponentPdkSource = resolvePdkSource;
        }

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

        // Wire up callbacks
        CanvasInteraction.OnSelectionChanged = comp =>
        {
            RightPanel.Sweep.ConfigureForComponent(comp, Canvas);
            LeftPanel.HierarchyPanel.SyncSelectionFromCanvas(comp);
        };

        // Carry per-instance Nazca overrides onto pasted copies so their raw-code
        // preview and export geometry follow the duplicated component.
        CanvasInteraction.OnComponentsPasted = identifierMap =>
            Selection.NazcaOverridePropagator.Propagate(
                identifierMap, FileOperations.StoredNazcaOverrides);

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
                    StatusText = $"Failed to load template '{template.Name}': {ex.Message}";
                    BottomPanel.ErrorConsole.Log($"Failed to load template '{template.Name}': {ex.Message}", CAP_Contracts.Logger.LogLevel.Error, ex);
                    return;
                }

                if (template.TemplateGroup == null)
                {
                    StatusText = $"Template '{template.Name}' could not be loaded - file may be corrupted";
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

        // Export validation must run against the SAME per-instance Nazca overrides the
        // production export uses; FileOperations owns the live store (issue #565 F1).
        RightPanel.ExportValidation.OverridesProvider = () => FileOperations.StoredNazcaOverrides;
        FileOperations.ZoomToFitAfterLoad = (w, h) =>
        {
            var (vpWidth, vpHeight) = ViewportControl.GetViewportSize?.Invoke() ?? (w, h);
            ViewportControl.ZoomToFit(vpWidth, vpHeight);
        };

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
        IsPlayground = p?.IsPlayground == true;
        ActiveProcessLabel = p == null ? "No process selected"
            : p.IsPlayground ? "Playground — not manufacturable"
            : $"Process: {p.DisplayName}";
        LeftPanel.ApplyActiveProcess(p);
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
    /// Raised when the user requests to open the Fabrication Process window
    /// (process model — issue #570). The View layer subscribes and shows it.
    /// </summary>
    public Action? ShowProcessManagerRequested { get; set; }

    [RelayCommand]
    private void OpenProcessManager()
    {
        ShowProcessManagerRequested?.Invoke();
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
            StatusText = "Opening PDK help documentation in browser...";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open browser: {ex.Message}";
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
            StatusText = $"Undone: {CommandManager.RedoDescription ?? "action"}";
        }
        else
        {
            StatusText = "Nothing to undo";
        }
    }

    private bool CanUndo() => CommandManager.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (CommandManager.Redo())
        {
            StatusText = $"Redone: {CommandManager.UndoDescription ?? "action"}";
        }
        else
        {
            StatusText = "Nothing to redo";
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
            StatusText = "Simulation overlay OFF";
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
            StatusText = "Running simulation...";
            var result = await Simulation.RunAsync(Canvas);

            if (result.Success)
            {
                StatusText = $"Simulation complete: {result.LightSourceCount} source(s), " +
                             $"{result.ConnectionCount} connections @ {result.WavelengthSummary}";

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
        catch (Exception ex)
        {
            StatusText = $"Simulation error: {ex.Message}";
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

        RightPanel.DesignValidation.RunValidation(
            connections,
            groups,
            allComponents,
            ChipSize.CurrentWidthMicrometers,
            ChipSize.CurrentHeightMicrometers);

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
    /// Per-instance Nazca function parameter overrides, keyed by component Identifier.
    /// Null or empty for designs without Nazca overrides.
    /// Each entry stores the overridden function name and parameters plus the original
    /// template values to allow "Reset to template" after a project reload.
    /// </summary>
    public Dictionary<string, CAP_DataAccess.Persistence.PIR.NazcaCodeOverride>? NazcaOverrides { get; set; }

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
    public double? SliderValue { get; set; }
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
    public double? SliderValue { get; set; }
    public int? LaserWavelengthNm { get; set; }
    public double? LaserPower { get; set; }
    public bool? IsLocked { get; set; }
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
    public bool? IsLocked { get; set; }
    public double? TargetLengthMicrometers { get; set; }
    public bool? IsTargetLengthEnabled { get; set; }
    public double? LengthToleranceMicrometers { get; set; }
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
