using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Notifications;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Analysis.OnaAnalysis;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_Core.Components.Core;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkImport;
using CAP.Avalonia.ViewModels.Process;
using CAP.Avalonia.Views.Dialogs;
using CAP.Avalonia.Views.PdkImport;
using CAP.Avalonia.ViewModels.Solvers;
using System.ComponentModel;
using System.Linq;

namespace CAP.Avalonia.Views;

public partial class MainWindow : Window
{
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();

        // Set up the FileDialogService when the window is loaded
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                WireSettingsOpener(vm); // see MainWindow.SettingsOpener.cs
                AttachNotificationHost();

                vm.FileDialogService = new FileDialogService(this);
                vm.FileOperations.MessageBoxService = new MessageBoxService();
                vm.RightPanel.Sweep.FileDialogService = vm.FileDialogService;
                vm.RightPanel.OnaAnalysis.FileDialogService = vm.FileDialogService;
                vm.RightPanel.OnaAnalysis.OpenWindowAsync = analyzer => OpenOnaAnalyzerWindow(analyzer, vm);
                // Wire the per-component editor for analyzers so the right-panel
                // properties section can also open the ONA tool window.
                var onaEditorProvider = App.Services.GetService(
                    typeof(CAP.Avalonia.ViewModels.Properties.Editors.OnaAnalyzerEditorProvider))
                    as CAP.Avalonia.ViewModels.Properties.Editors.OnaAnalyzerEditorProvider;
                if (onaEditorProvider != null)
                    onaEditorProvider.OpenSweepAsync = analyzer => OpenOnaAnalyzerWindow(analyzer, vm);
                vm.RightPanel.RoutingDiagnostics.FileDialogService = vm.FileDialogService;
                vm.BottomPanel.Analysis.Transient.FileDialogService = vm.FileDialogService;
                vm.BottomPanel.Analysis.Eye.FileDialogService = vm.FileDialogService;
                ExportDialogWiring.Wire(vm, this, vm.ErrorConsole);
                vm.ViewportControl.GetViewportSize = GetActualViewportSize;

                // Wire up rename dialog for group templates
                vm.LeftPanel.ComponentLibrary.ShowRenameDialogAsync = async (currentName) =>
                {
                    var dialog = new RenameDialog(currentName);
                    return await dialog.ShowDialog<string?>(this);
                };

                // Wire up PDK Import Wizard for Nazca .py files
                var importService = App.Services.GetService(typeof(PdkImportService)) as PdkImportService;
                if (importService != null)
                {
                    vm.LeftPanel.ShowImportWizardAsync = async (pyFilePath) =>
                    {
                        var wizardVm = new PdkImportWizardViewModel(pyFilePath, importService);
                        var wizard = new PdkImportWizardWindow { DataContext = wizardVm };
                        return await wizard.ShowDialog<string?>(this);
                    };
                }

                // Wire up PDK Offset Editor window
                vm.ShowPdkOffsetEditorRequested = () =>
                {
                    var editorVm = vm.PdkOffsetEditor;
                    editorVm.FileDialogService = new FileDialogService(this);
                    var editorWindow = new PdkOffsetEditorWindow
                    {
                        DataContext = editorVm
                    };
                    editorWindow.Show(this);
                };

                // Wire up Fabrication Process window (process model — #570). The dialog derives
                // its state from the design's active process + loaded PDKs at open time, so a
                // reopened dialog always reflects the current selection (#660).
                vm.ShowProcessManagerRequested = () =>
                {
                    var processVm = new ProcessManagementViewModel(new FileDialogService(this));
                    processVm.ShowActiveProcess(vm.FileOperations.ActiveProcess, vm.LeftPanel.GetLoadedPdkDrafts());
                    var processWindow = new ProcessManagementWindow
                    {
                        DataContext = processVm
                    };
                    processWindow.Show(this);
                };

                // Wire up the New-Design process-selection dialog (issue #570)
                vm.ShowProcessSelectionAsync = async groups =>
                {
                    var pvm = new ProcessSelectionViewModel(groups);
                    var dlg = new ProcessSelectionDialog { DataContext = pvm };
                    await dlg.ShowDialog(this);
                    return pvm.Result;
                };

                // Prompt for the fabrication process once at startup (issue #570). Deferred so
                // the main window is fully shown before the modal picker opens; dismissing it
                // starts in Playground.
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    async () => await vm.PromptForInitialProcessAsync(),
                    global::Avalonia.Threading.DispatcherPriority.Background);

                // Wire up clipboard for RoutingDiagnostics
                vm.RightPanel.RoutingDiagnostics.CopyToClipboard = async (text) =>
                {
                    var clipboard = Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(text);
                    }
                };

                // Wire up clipboard for DimensionValidator
                vm.RightPanel.DimensionValidator.CopyToClipboard = async (text) =>
                {
                    var clipboard = Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(text);
                    }
                };

                // Wire up clipboard for ErrorConsole
                vm.BottomPanel.ErrorConsole.CopyToClipboard = async (text) =>
                {
                    var clipboard = Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(text);
                    }
                };

                // Wire up auto-scroll: scroll to the newest entry when entries are added
                vm.BottomPanel.ErrorConsole.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(vm.BottomPanel.ErrorConsole.EntryCount) && ErrorConsoleListBox != null)
                    {
                        var items = ErrorConsoleListBox.ItemsSource;
                        if (items is System.Collections.IList list && list.Count > 0)
                        {
                            ErrorConsoleListBox.ScrollIntoView(list[list.Count - 1]);
                        }
                    }
                };

                // Wire up Component Settings dialog for hierarchy nodes
                vm.LeftPanel.HierarchyPanel.OpenComponentSettings = node =>
                {
                    ShowComponentSettingsDialog(
                        node.Component.Identifier,
                        node.Component.HumanReadableName ?? node.Component.Identifier,
                        node.Component,
                        vm);
                };

                // Wire up Component Settings dialog for canvas context menu
                vm.CanvasInteraction.OpenComponentSettings = compVm =>
                {
                    ShowComponentSettingsDialog(
                        compVm.Component.Identifier,
                        compVm.Component.HumanReadableName ?? compVm.Component.Identifier,
                        compVm.Component,
                        vm);
                };

                // Wire up per-instance S-matrix override marker in hierarchy
                vm.LeftPanel.HierarchyPanel.CheckHasSMatrixOverride =
                    id => vm.FileOperations.StoredSMatrices.ContainsKey(id);

                // Wire up per-instance Nazca override marker in hierarchy
                vm.LeftPanel.HierarchyPanel.CheckHasNazcaOverride =
                    id => vm.FileOperations.StoredNazcaOverrides.ContainsKey(id);

                // Initial badge population for PDK templates (covers user-global
                // overrides loaded from disk on app start). Updated again every
                // time the dialog mutates the user store, see ShowComponentSettingsDialog.
                RefreshTemplateOverrideBadges(vm);

                // Wire up GridSplitter resize events
                SetupPanelResizing(vm);

                // Wire up LeftPanel.SelectedGroupTemplate changes to update ListBox selections
                vm.LeftPanel.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(vm.LeftPanel.SelectedGroupTemplate))
                    {
                        UpdateGroupTemplateListBoxSelections(vm.LeftPanel.SelectedGroupTemplate);
                    }
                };
            }
        };
    }

    /// <summary>
    /// Creates the toast host for transient, non-error feedback (issue #586)
    /// and connects it to the app-wide <see cref="NotificationService"/> so
    /// ViewModels can raise auto-dismissing popups on the right side of the
    /// window instead of opening the error console.
    /// </summary>
    private void AttachNotificationHost()
    {
        const int maxVisibleToasts = 3;
        var manager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = maxVisibleToasts
        };

        var service = App.Services.GetService(typeof(NotificationService)) as NotificationService;
        service?.Attach(manager);
    }

    /// <summary>
    /// Sets up panel resizing by setting initial widths and listening to GridSplitter DragCompleted events.
    /// </summary>
    private void SetupPanelResizing(MainViewModel vm)
    {
        // Set initial widths from saved preferences
        if (LeftPanelGrid != null && LeftPanelGrid.ColumnDefinitions.Count > 0)
        {
            LeftPanelGrid.ColumnDefinitions[0].Width = new GridLength(vm.LeftPanel.LeftPanelWidth.Value, GridUnitType.Pixel);
        }

        if (RightPanelGrid != null && RightPanelGrid.ColumnDefinitions.Count > 1)
        {
            RightPanelGrid.ColumnDefinitions[1].Width = new GridLength(vm.RightPanel.RightPanelWidth.Value, GridUnitType.Pixel);
        }

        // Listen to GridSplitter drag events to save new widths
        // Left panel resizing - we need to find the GridSplitter in LeftPanelGrid
        if (LeftPanelGrid != null)
        {
            var leftSplitter = LeftPanelGrid.Children.OfType<GridSplitter>().FirstOrDefault();
            if (leftSplitter != null)
            {
                leftSplitter.DragCompleted += (s, e) =>
                {
                    if (LeftPanelGrid.ColumnDefinitions.Count > 0)
                    {
                        var newWidth = LeftPanelGrid.ColumnDefinitions[0].Width.Value;
                        if (newWidth > 0)
                        {
                            vm.LeftPanel.LeftPanelWidth = new GridLength(newWidth);
                        }
                    }
                };
            }
        }

        // Right panel resizing
        if (RightPanelGrid != null)
        {
            var rightSplitter = RightPanelGrid.Children.OfType<GridSplitter>().FirstOrDefault();
            if (rightSplitter != null)
            {
                rightSplitter.DragCompleted += (s, e) =>
                {
                    if (RightPanelGrid.ColumnDefinitions.Count > 1)
                    {
                        var newWidth = RightPanelGrid.ColumnDefinitions[1].Width.Value;
                        if (newWidth > 0)
                        {
                            vm.RightPanel.RightPanelWidth = new GridLength(newWidth);
                        }
                    }
                };
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (DataContext is not MainViewModel mainVm) return;

        // Don't intercept keystrokes when a text input has focus (e.g., search box)
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        var ctrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Global keyboard shortcuts that work regardless of focus
        switch (e.Key)
        {
            case Key.N:
                if (ctrlPressed)
                    mainVm.NewProjectCommand.Execute(null);
                break;
            case Key.S:
                if (ctrlPressed)
                    mainVm.SaveDesignCommand.Execute(null);
                else
                    mainVm.SetSelectModeCommand.Execute(null);
                break;
            case Key.C:
                if (ctrlPressed)
                {
                    Console.WriteLine("DEBUG: Ctrl+C detected");
                    mainVm.CopySelectedCommand.Execute(null);
                }
                else
                    mainVm.SetConnectModeCommand.Execute(null);
                break;
            case Key.V:
                if (ctrlPressed)
                {
                    Console.WriteLine("DEBUG: Ctrl+V detected");
                    // Get the last canvas position for paste-at-cursor
                    var canvasPos = DesignCanvasControl.LastCanvasPosition;
                    mainVm.PasteSelected(canvasPos.X, canvasPos.Y);
                }
                break;
            case Key.D:
                if (!ctrlPressed)
                    mainVm.SetDeleteModeCommand.Execute(null);
                break;
            case Key.Delete:
            case Key.Back:
                mainVm.DeleteSelectedCommand.Execute(null);
                break;
            case Key.Escape:
                // First priority: Exit group edit mode if active (via command for undo/redo)
                if (mainVm.Canvas.IsInGroupEditMode)
                {
                    if (mainVm.Canvas.CurrentEditGroup != null)
                    {
                        var exitCmd = new Commands.ExitGroupEditModeCommand(
                            mainVm.Canvas, mainVm.Canvas.CurrentEditGroup);
                        mainVm.CommandManager.ExecuteCommand(exitCmd);
                    }
                    else
                    {
                        mainVm.Canvas.ExitGroupEditMode();
                    }
                    mainVm.StatusText = "Exited group edit mode";
                }
                else
                {
                    mainVm.SetSelectModeCommand.Execute(null);
                }
                break;
            case Key.Z:
                if (ctrlPressed)
                    mainVm.UndoCommand.Execute(null);
                break;
            case Key.Y:
                if (ctrlPressed)
                    mainVm.RedoCommand.Execute(null);
                break;
            case Key.R:
                if (!ctrlPressed)
                    mainVm.RotateSelectedCommand.Execute(null);
                break;
            case Key.G:
                if (!ctrlPressed)
                {
                    var canvas = mainVm.Canvas;
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        canvas.ShowGridOverlay = !canvas.ShowGridOverlay;
                    }
                    else
                    {
                        canvas.GridSnap.Toggle();
                        mainVm.StatusText = canvas.GridSnap.IsEnabled
                            ? $"Grid snap ON ({canvas.GridSnap.GridSizeMicrometers}\u00b5m)"
                            : "Grid snap OFF";
                    }
                }
                break;
            case Key.F:
                if (!ctrlPressed)
                {
                    var (width, height) = GetActualViewportSize();
                    mainVm.ZoomToFit(width, height);
                }
                break;
            case Key.P:
                if (!ctrlPressed)
                {
                    var canvasVm = mainVm.Canvas;
                    if (!canvasVm.ShowPowerFlow)
                    {
                        if (canvasVm.PowerFlowVisualizer.CurrentResult == null)
                            mainVm.RunSimulationCommand.Execute(null);
                        else
                        {
                            canvasVm.ShowPowerFlow = true;
                            canvasVm.PowerFlowVisualizer.IsEnabled = true;
                        }
                        mainVm.StatusText = "Power flow overlay: ON (auto-updates on changes)";
                    }
                    else
                    {
                        canvasVm.ShowPowerFlow = false;
                        canvasVm.PowerFlowVisualizer.IsEnabled = false;
                        mainVm.StatusText = "Power flow overlay: OFF";
                    }
                }
                break;
            case Key.L:
                if (!ctrlPressed)
                    mainVm.RunSimulationCommand.Execute(null);
                break;
            default:
                return; // Don't mark as handled for unrecognized keys
        }

        e.Handled = true;
        DesignCanvasControl.InvalidateVisual();
    }

    /// <summary>
    /// Opens the "Compute Modes for Waveguide" dialog from the Tools menu.
    /// </summary>
    private void OpenModeSolverDialog_Click(object? sender, RoutedEventArgs e)
    {
        var vm = App.Services.GetService(typeof(ModeSolverViewModel)) as ModeSolverViewModel;
        if (vm == null) return;
        var dialog = new ModeSolverDialog { DataContext = vm };
        dialog.Show(this);
    }

    private void ZoomToFitButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var (width, height) = GetActualViewportSize();
            vm.ZoomToFit(width, height);
        }
    }

    /// <summary>
    /// Gets the actual viewport size (visible area) independent of zoom level.
    /// Uses the DesignCanvas control's own layout bounds, which correctly excludes
    /// the left panel, right panel, and toolbar from the viewport dimensions.
    /// The rendering coordinate space is the canvas local space, so ZoomToFit
    /// must use canvas dimensions — not window dimensions — for correct centering.
    /// </summary>
    private (double width, double height) GetActualViewportSize()
    {
        // Use the canvas control's actual layout bounds.
        // This is correct because PanX/PanY are in canvas-local coordinates,
        // and ZoomToFit computes pan as: vpWidth/2 - boxCenterX * zoom.
        // Using window ClientSize (which includes sidebars) would shift the
        // computed pan center by (windowWidth - canvasWidth) / 2, causing the
        // "wrong position on first F-press" bug.
        var canvasBounds = DesignCanvasControl.Bounds;
        if (canvasBounds.Width > 0 && canvasBounds.Height > 0)
            return (canvasBounds.Width, canvasBounds.Height);

        // Fallback: if the canvas has not been laid out yet, use window client size.
        var windowWidth = ClientSize.Width;
        var windowHeight = ClientSize.Height;
        if (windowWidth > 0 && windowHeight > 0)
            return (windowWidth, windowHeight);

        return (1400, 900); // Last-resort default matching the initial window size
    }

    /// <summary>
    /// Handles pointer entering a group template item (shows delete button).
    /// </summary>
    private void OnGroupItemPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.DataContext is GroupTemplateItemViewModel itemVm)
        {
            itemVm.IsHovered = true;
        }
    }

    /// <summary>
    /// Handles pointer leaving a group template item (hides delete button).
    /// </summary>
    private void OnGroupItemPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.DataContext is GroupTemplateItemViewModel itemVm)
        {
            itemVm.IsHovered = false;
        }
    }

    /// <summary>
    /// Handles selection change in UserGroups ListBox.
    /// Extracts the GroupTemplate from GroupTemplateItemViewModel and sets it in LeftPanel.
    /// </summary>
    private void OnUserGroupsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox listBox) return;

        if (listBox.SelectedItem is GroupTemplateItemViewModel itemVm)
        {
            vm.LeftPanel.SelectedGroupTemplate = itemVm.Template;
            // Clear PDK groups selection
            ClearPdkGroupsSelection();
        }
        else if (listBox.SelectedItem == null)
        {
            // Only clear if this was triggered by user action, not by code
            if (e.RemovedItems.Count > 0 && e.AddedItems.Count == 0)
            {
                vm.LeftPanel.SelectedGroupTemplate = null;
            }
        }
    }

    /// <summary>
    /// Handles selection change in PdkGroups ListBox.
    /// Extracts the GroupTemplate from GroupTemplateItemViewModel and sets it in LeftPanel.
    /// </summary>
    private void OnPdkGroupsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox listBox) return;

        if (listBox.SelectedItem is GroupTemplateItemViewModel itemVm)
        {
            vm.LeftPanel.SelectedGroupTemplate = itemVm.Template;
            // Clear user groups selection
            ClearUserGroupsSelection();
        }
        else if (listBox.SelectedItem == null)
        {
            // Only clear if this was triggered by user action, not by code
            if (e.RemovedItems.Count > 0 && e.AddedItems.Count == 0)
            {
                vm.LeftPanel.SelectedGroupTemplate = null;
            }
        }
    }

    /// <summary>
    /// Refreshes the 📊 user-global-override badges on every PDK template in the
    /// library list. Called on initial wire-up and after every dialog mutation in
    /// template mode so the badge tracks the on-disk store without manual reloads.
    /// </summary>
    private static void RefreshTemplateOverrideBadges(MainViewModel vm)
    {
        var userStore = App.Services.GetService(typeof(UserSMatrixOverrideStore))
            as UserSMatrixOverrideStore;
        if (userStore == null) return;

        vm.LeftPanel.RefreshUserGlobalOverrideBadges(userStore.Overrides.ContainsKey);
    }

    /// <summary>
    /// Handles "Component Settings…" click in the PDK template list context menu.
    /// </summary>
    private void TemplateComponentSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (sender is MenuItem { DataContext: ComponentTemplate template })
        {
            var key = $"{template.PdkSource}::{template.Name}";
            ShowComponentSettingsDialog(key, template.Name, null, vm, template);
        }
    }

    /// <summary>
    /// Creates and shows the Component Settings dialog for the given entity.
    ///
    /// Per-Instance mode (<paramref name="liveComponent"/> non-null): the dialog
    /// reads/writes <c>FileOperations.StoredSMatrices</c>, so the override is
    /// scoped to this canvas instance and persisted in the .lun file.
    ///
    /// Per-Template mode (<paramref name="liveComponent"/> null): the dialog
    /// reads/writes the user-global <see cref="UserSMatrixOverrideStore"/>, so
    /// the override applies to every instance of that template across every
    /// project the user opens. After a successful import/delete the store is
    /// flushed to disk and live components matching the template are
    /// re-applied so the change takes effect immediately without reloading.
    /// </summary>
    private void ShowComponentSettingsDialog(
        string entityKey,
        string displayName,
        CAP_Core.Components.Core.Component? liveComponent,
        MainViewModel vm,
        ComponentTemplate? templateForDefaults = null)
    {
        var errorConsole = App.Services.GetService(typeof(CAP_Core.ErrorConsoleService))
            as CAP_Core.ErrorConsoleService;
        var userStore = App.Services.GetService(typeof(UserSMatrixOverrideStore))
            as UserSMatrixOverrideStore;
        var portMappingDialog = App.Services.GetService(typeof(IPortMappingDialogService))
            as IPortMappingDialogService;

        // FDTD "Recalculate S-matrix": wire the solver service and a factory that
        // renders the component's geometry/pins into an FDTD request. Both are
        // optional — the dialog hides the recompute button when they're absent.
        var fdtdService = App.Services.GetService(typeof(CAP_Core.Solvers.Fdtd.IFdtdSMatrixService))
            as CAP_Core.Solvers.Fdtd.IFdtdSMatrixService;
        var previewService = App.Services.GetService(typeof(CAP_Core.Export.NazcaComponentPreviewService))
            as CAP_Core.Export.NazcaComponentPreviewService;
        Func<CAP_Core.Components.Core.Component, CancellationToken, Task<CAP_Core.Solvers.Fdtd.FdtdSMatrixRequest?>>? fdtdRequestFactory = null;
        if (fdtdService != null && previewService != null)
        {
            var requestFactory = new CAP.Avalonia.Services.Solvers.ComponentFdtdRequestFactory(previewService);
            fdtdRequestFactory = (component, ct) => requestFactory.BuildAsync(component, ct);
        }

        var notificationService = App.Services.GetService(typeof(INotificationService))
            as INotificationService;

        var dialogVm = new ComponentSettingsDialogViewModel(
            new FileDialogService(this),
            errorConsole,
            importers: null,
            portMappingDialog: portMappingDialog,
            fdtdService: fdtdService,
            fdtdRequestFactory: fdtdRequestFactory,
            notificationService: notificationService);

        bool isTemplateMode = liveComponent == null && userStore != null;
        var store = isTemplateMode
            ? userStore!.Overrides
            : vm.FileOperations.StoredSMatrices;

        Action onChanged = isTemplateMode
            ? () =>
              {
                  userStore!.Save();
                  vm.FileOperations.ReapplyTemplateOverrides();
                  vm.LeftPanel.HierarchyPanel.RefreshOverrideMarkers();
                  RefreshTemplateOverrideBadges(vm);
              }
            : () => vm.LeftPanel.HierarchyPanel.RefreshOverrideMarkers();

        // Effective S-matrix data feeds the read-only "Currently effective" section.
        // Per-Instance: read straight off the live component (its WaveLengthToSMatrixMap
        // is exactly what the simulator will use, including any override already applied).
        // Per-Template: build a throwaway component from the template so we can show
        // the PDK default without requiring a canvas instance.
        Dictionary<int, CAP_Core.LightCalculation.SMatrix>? effectiveSMatrices = null;
        IReadOnlyList<CAP_Core.Components.Core.Pin>? effectivePins = null;
        IReadOnlyList<string>? availablePinNames = null;
        if (liveComponent != null)
        {
            effectiveSMatrices = liveComponent.WaveLengthToSMatrixMap;
            effectivePins = liveComponent.PhysicalPins
                .Where(pp => pp.LogicalPin != null)
                .Select(pp => pp.LogicalPin!)
                .ToList();
            // Pin-name list drives the port-mapping dialog. Use PhysicalPin
            // names (what the user sees in the UI), not the LogicalPin's
            // internal id, so the dialog matches the rest of the dialog.
            availablePinNames = liveComponent.PhysicalPins
                .Where(pp => pp.LogicalPin != null)
                .Select(pp => pp.Name)
                .ToList();
        }
        else if (templateForDefaults != null)
        {
            var tempInstance = ComponentTemplates.CreateFromTemplate(templateForDefaults, 0, 0);
            effectiveSMatrices = tempInstance.WaveLengthToSMatrixMap;
            effectivePins = tempInstance.PhysicalPins
                .Where(pp => pp.LogicalPin != null)
                .Select(pp => pp.LogicalPin!)
                .ToList();
            availablePinNames = templateForDefaults.PinDefinitions
                .Select(pd => pd.Name)
                .ToList();
        }

        // Resolve Nazca template values for per-instance mode.
        // When no override is stored yet, the live component's current values ARE the template values.
        // When an override was applied from a previous session, use the saved template reference
        // from within the stored override record so "Reset to template" always targets the
        // correct PDK defaults rather than the already-overridden live values.
        string? templateFunctionName = null;
        string? templateFunctionParameters = null;
        string? templateModuleName = null;
        if (liveComponent != null)
        {
            if (vm.FileOperations.StoredNazcaOverrides.TryGetValue(entityKey, out var existingNazca))
            {
                templateFunctionName = existingNazca.TemplateFunctionName ?? liveComponent.NazcaFunctionName;
                templateFunctionParameters = existingNazca.TemplateFunctionParameters ?? liveComponent.NazcaFunctionParameters;
                templateModuleName = existingNazca.TemplateModuleName ?? liveComponent.NazcaModuleName;
            }
            else
            {
                templateFunctionName = liveComponent.NazcaFunctionName;
                templateFunctionParameters = liveComponent.NazcaFunctionParameters;
                templateModuleName = liveComponent.NazcaModuleName;
            }
        }

        // Per-instance raw Nazca code editor (issue #556) — only in per-instance mode.
        var nazcaPreviewService = App.Services.GetService(typeof(CAP_Core.Export.NazcaComponentPreviewService))
            as CAP_Core.Export.NazcaComponentPreviewService;
        var gdsFactoryPreviewService = App.Services.GetService(typeof(CAP_Core.Export.GdsFactoryComponentPreviewService))
            as CAP_Core.Export.GdsFactoryComponentPreviewService;
        string? nazcaTemplateCode = null;
        Func<double, double, IReadOnlyList<string>>? nazcaOverlapCheck = null;
        Action? nazcaDimensionsChanged = null;
        Action<IReadOnlyList<CAP_Core.Components.Core.PhysicalPin>>? nazcaPinsChanged = null;
        if (liveComponent != null && !isTemplateMode)
        {
            nazcaTemplateCode = NazcaCodeTemplateBuilder.Build(
                templateModuleName, templateFunctionName, templateFunctionParameters);
            nazcaOverlapCheck = (w, h) => FindOverlappingComponentNames(vm, liveComponent, w, h);
            nazcaDimensionsChanged = () =>
            {
                var compVm = vm.Canvas.Components.FirstOrDefault(c => c.Component == liveComponent);
                compVm?.NotifyDimensionsChanged();
                // Repaint the canvas immediately so the resized footprint shows on Apply.
                DesignCanvasControl.InvalidateVisual();
            };
            nazcaPinsChanged = _ =>
            {
                // Issue #561: Connections auf die neuen Override-Pins umhaengen bzw.
                // mit Warnung trennen, Pin-VMs auffrischen, Routen + Simulation neu.
                var warnings = vm.Canvas.OnComponentPinsChanged(liveComponent);
                foreach (var warning in warnings)
                    errorConsole?.LogWarning(warning);
                DesignCanvasControl.InvalidateVisual();
            };
        }

        // S-matrix overrides (FDTD recompute / file import) are stored under the
        // component's geometry identity so a copy inherits them; the per-instance
        // Nazca raw-code override keeps using entityKey (component.Identifier).
        // The library/template path has no live component and keeps the {PdkSource}::{Name} key.
        // Resolve lazily so the dialog can re-derive the key after a Nazca geometry override
        // (raw code / parameters) changes the identity mid-session.
        Func<string> smatrixKeyResolver = liveComponent != null
            ? () => CAP.Avalonia.Services.ComponentGeometryKey.For(
                liveComponent,
                c => vm.FileOperations.StoredNazcaOverrides.TryGetValue(c.Identifier, out var o) ? o.RawCode : null)
            : () => entityKey;
        string smatrixKey = smatrixKeyResolver();

        dialogVm.Configure(
            entityKey,
            smatrixKey,
            displayName,
            store,
            liveComponent,
            onChanged: onChanged,
            isUserGlobalScope: isTemplateMode,
            effectiveSMatrices: effectiveSMatrices,
            effectivePins: effectivePins,
            availablePinNames: availablePinNames,
            storedNazcaOverrides: isTemplateMode ? null : vm.FileOperations.StoredNazcaOverrides,
            templateFunctionName: templateFunctionName,
            templateFunctionParameters: templateFunctionParameters,
            templateModuleName: templateModuleName,
            nazcaPreviewService: nazcaPreviewService,
            nazcaTemplateCode: nazcaTemplateCode,
            nazcaOverlapCheck: nazcaOverlapCheck,
            nazcaDimensionsChanged: nazcaDimensionsChanged,
            nazcaPinsChanged: nazcaPinsChanged,
            smatrixKeyResolver: smatrixKeyResolver,
            gdsFactoryPreviewService: gdsFactoryPreviewService);

        var dialog = new ComponentSettingsDialog { DataContext = dialogVm };
        dialog.Show(this);
    }

    /// <summary>
    /// Returns the display names of canvas components the given instance would overlap
    /// if resized to <paramref name="width"/> × <paramref name="height"/> at its current
    /// position. Non-blocking advisory used by the Nazca code editor's overlap warning.
    /// </summary>
    private static IReadOnlyList<string> FindOverlappingComponentNames(
        MainViewModel vm, CAP_Core.Components.Core.Component liveComponent, double width, double height)
    {
        var compVm = vm.Canvas.Components.FirstOrDefault(c => c.Component == liveComponent);
        if (compVm == null)
            return System.Array.Empty<string>();

        // CanPlaceComponent returns false on ANY overlap or chip-boundary violation;
        // when it reports a clash, enumerate the specific neighbours for the message.
        if (vm.Canvas.CanPlaceComponent(compVm.X, compVm.Y, width, height, excludeComponent: compVm))
            return System.Array.Empty<string>();

        var names = new List<string>();
        foreach (var other in vm.Canvas.Components)
        {
            if (other == compVm) continue;
            bool overlaps = compVm.X < other.X + other.Width &&
                            compVm.X + width > other.X &&
                            compVm.Y < other.Y + other.Height &&
                            compVm.Y + height > other.Y;
            if (overlaps)
                names.Add(other.Component.HumanReadableName ?? other.Component.Identifier);
        }
        return names;
    }

    private void ClearUserGroupsSelection()
    {
        if (UserGroupsListBox != null)
        {
            UserGroupsListBox.SelectedItem = null;
        }
    }

    private void ClearPdkGroupsSelection()
    {
        if (PdkGroupsListBox != null)
        {
            PdkGroupsListBox.SelectedItem = null;
        }
    }

    /// <summary>
    /// Clears both user and PDK group selections. Called from MainViewModel.
    /// </summary>
    public void ClearAllGroupSelections()
    {
        ClearUserGroupsSelection();
        ClearPdkGroupsSelection();
    }

    /// <summary>
    /// Updates ListBox selections to match the given GroupTemplate.
    /// Finds the corresponding GroupTemplateItemViewModel and selects it.
    /// </summary>
    private void UpdateGroupTemplateListBoxSelections(CAP_Core.Components.Creation.GroupTemplate? template)
    {
        if (DataContext is not MainViewModel vm) return;

        if (template == null)
        {
            // Clear all selections
            ClearAllGroupSelections();
        }
        else
        {
            // Find and select the matching item in UserGroups
            var userItem = vm.LeftPanel.ComponentLibrary.UserGroups.FirstOrDefault(vm => vm.Template == template);
            if (userItem != null)
            {
                if (UserGroupsListBox != null)
                {
                    UserGroupsListBox.SelectedItem = userItem;
                }
                ClearPdkGroupsSelection();
                return;
            }

            // Find and select the matching item in PdkGroups
            var pdkItem = vm.LeftPanel.ComponentLibrary.PdkGroups.FirstOrDefault(vm => vm.Template == template);
            if (pdkItem != null)
            {
                if (PdkGroupsListBox != null)
                {
                    PdkGroupsListBox.SelectedItem = pdkItem;
                }
                ClearUserGroupsSelection();
            }
        }
    }

    /// <summary>
    /// Opens a new ONA Analyzer tool window bound to the given analyzer component.
    /// Each call creates a fresh <see cref="OnaSweepViewModel"/> so several
    /// analyzers can be inspected side-by-side; the window is non-modal.
    /// </summary>
    private System.Threading.Tasks.Task OpenOnaAnalyzerWindow(CAP_Core.Components.Core.Component analyzer, MainViewModel vm)
    {
        var sweepVm = new OnaSweepViewModel(vm.ErrorConsole) { Analyzer = analyzer };
        sweepVm.Configure(vm.Canvas);
        sweepVm.FileDialogService = vm.FileDialogService;
        var window = new OnaAnalyzerWindow { DataContext = sweepVm };
        window.Show(this);
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
