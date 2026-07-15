using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Notifications;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Analysis.OnaAnalysis;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_Core.Components.Core;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkImport;
using CAP.Avalonia.ViewModels.Process;
using CAP.Avalonia.Views.Dialogs;
using CAP.Avalonia.Views.PdkImport;
using CAP.Avalonia.ViewModels.Solvers;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace CAP.Avalonia.Views;

public partial class MainWindow : Window
{
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// Tracks the currently-open per-PDK "Edit Process" window, keyed by the PDK's
    /// file path (falling back to its name when the path is null, e.g. an
    /// unsaved draft). Prevents a second click on the same PDK's "Edit…" button
    /// from opening a duplicate editor window — the existing one is activated
    /// instead. Entries are removed when their window closes.
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<string, ProcessManagementWindow> _openPdkEditWindows = new();

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
                vm.RightPanel.Netlist.FileDialogService = vm.FileDialogService;
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

                // Wire up the "New Component" window (issue #656) — non-modal, like the
                // Fabrication Process and ONA Analyzer tool windows, so the user can keep
                // iterating on the design while it stays open.
                vm.LeftPanel.ShowNewComponentWindowAsync = newComponentVm =>
                {
                    // Own-code mode's "Load from .py…" button (#custom-component-rawcode): the
                    // view model only knows the file's already-read contents (PickPyFile's
                    // contract), never a path, so it can't own a FileDialogService itself.
                    newComponentVm.PickPyFile = async () =>
                    {
                        var path = await new FileDialogService(this).ShowOpenFileDialogAsync(
                            "Load Python file", "Python Files (*.py)|*.py|All Files (*.*)|*.*");
                        return path is null ? null : await File.ReadAllTextAsync(path);
                    };
                    // Confirm before overwriting an existing component name in the target PDK
                    // (new or existing custom PDK — the message names whichever applies).
                    newComponentVm.ConfirmOverwrite = async (name, pdkName) =>
                    {
                        var choice = await new MessageBoxService().ShowChoicePromptAsync(
                            $"'{name}' already exists in PDK '{pdkName}'. Overwrite?",
                            "Overwrite?", new[] { "Cancel", "Overwrite" });
                        return choice == 1;
                    };
                    // "New PDK…" sentinel modal creation hook (#723/#727 follow-up, CP-T4): opens
                    // the purpose-built CreateCustomPdkWindow — not the general Fabrication
                    // Process editor — so creating a brand-new named PDK is a small, focused
                    // dialog instead of the full view/edit-existing-process tool. Modal
                    // (ShowDialog, owner = the New Component window itself) so its PDK dropdown
                    // cannot be left mid-selection while the modal is open.
                    var window = new NewComponentWindow { DataContext = newComponentVm };
                    // Save closes the window; closing the window (via Save, the titlebar X, or
                    // Alt+F4) always cancels any Meep compute still running so it doesn't keep
                    // burning CPU/Docker resources after the user has moved on.
                    newComponentVm.Saved += (_, _) => window.Close();
                    window.Closing += (_, _) => newComponentVm.CancelCompute();
                    newComponentVm.CreateNewPdk = async () =>
                    {
                        // Shared dialog wiring (see ShowCreateCustomPdkDialogAsync) — it also
                        // registers the new (possibly component-less) PDK into the library, so
                        // cancelling the component afterwards no longer leaves the PDK invisible
                        // until the next restart (PR #739 review).
                        var createdPath = await ShowCreateCustomPdkDialogAsync(window);
                        if (createdPath is null)
                            return null;

                        var userPdkStore = App.Services.GetService(typeof(UserPdkStore)) as UserPdkStore;
                        return userPdkStore?.ListCustomPdks().FirstOrDefault(i => i.FilePath == createdPath);
                    };
                    window.Show(this);
                    return System.Threading.Tasks.Task.CompletedTask;
                };

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

                // Wire up clipboard for the Netlist panel (#687)
                vm.RightPanel.Netlist.CopyToClipboard = async (text) =>
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

    /// <summary>
    /// Opens the "Check PDKs against Python" dialog from the Tools menu (issue #515).
    /// </summary>
    private void OpenPdkResolutionCheckDialog_Click(object? sender, RoutedEventArgs e)
    {
        var vm = App.Services.GetService(typeof(ViewModels.PdkResolution.PdkResolutionCheckViewModel))
            as ViewModels.PdkResolution.PdkResolutionCheckViewModel;
        if (vm == null) return;
        var dialog = new PdkResolutionCheckDialog { DataContext = vm };
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

    private void TemplateEditComponent_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (sender is Control { DataContext: ComponentTemplate template })
        {
            vm.LeftPanel.EditCustomComponentCommand.Execute(template);
        }
    }

    /// <summary>
    /// Handles "Delete…" click in the PDK template list context menu (LC-T5): confirms, then
    /// moves the component out of its user PDK file into <c>.trash</c> (backing up the pre-edit
    /// file) and out of the library via <see cref="LeftPanelViewModel.RemoveCustomComponentCommand"/>.
    /// Only wired to a visible/enabled menu item for custom (non-Foundry) templates — same
    /// <c>IsCustom</c> binding as "Edit…" — but repeats the authoritative
    /// <see cref="LeftPanelViewModel.CanEditTemplate"/> guard here before even showing the
    /// confirm dialog. Placed components on the canvas are never touched (Design Checks flag any
    /// resulting conflict).
    /// </summary>
    private async void TemplateDeleteComponent_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        // Accept both the context-menu item and the inline hover ✕ button.
        if (sender is not Control { DataContext: ComponentTemplate template } || !vm.LeftPanel.CanDeleteTemplate(template))
            return;

        var choice = await new MessageBoxService().ShowChoicePromptAsync(
            $"Move component '{template.Name}' to trash?\n\n"
            + "The PDK file is rewritten without this component; a full pre-edit backup "
            + "(including any hand-added JSON comments, which the rewrite does not preserve) is "
            + "saved to user-pdks/.trash first. Placed instances on the canvas are kept.",
            "Delete Component?", new[] { "Cancel", "Move to Trash" });
        if (choice != 1)
            return;

        vm.LeftPanel.RemoveCustomComponentCommand.Execute(template);
    }

    /// <summary>
    /// Handles "Edit…" click on a custom PDK's row in PDK Management (issue #726 follow-up):
    /// opens the Fabrication Process editor scoped to just that PDK's own process, replacing the
    /// old toolbar-wide dialog with its preset/import pickers. Bundled PDKs have no Edit button
    /// (see the <c>!IsBundled</c> visibility binding in <c>MainWindow.axaml</c>), but the check is
    /// repeated here as the authoritative guard. Never touches
    /// <c>FileOperationsViewModel.ActiveProcess</c> — only the PDK's own JSON file.
    /// </summary>
    private void PdkEditProcess_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PdkInfoViewModel pdk } || pdk.IsBundled)
            return;
        if (DataContext is not MainViewModel vm)
            return;

        // Match by file path first, not just display name: two loaded PDKs can share a name
        // (e.g. two custom PDKs authored under the same name from different files), and a
        // name-only lookup could then load the wrong draft here while still resolving
        // PdkFilePathResolver to this row's OWN file below — silently writing this edit into a
        // different PDK's JSON (issue #733 review, Finding 5).
        var draft = MetalTraceStyleResolver.FindOwnDraft(vm.LeftPanel.GetLoadedPdkDrafts(), pdk.FilePath, pdk.Name);
        if (draft is null)
            return;

        // Dedup: a second click on the same PDK's "Edit…" button re-activates the
        // already-open editor instead of spawning a duplicate window. Deliberately shows the
        // window's CURRENT (possibly unsaved) editor state rather than reloading the draft —
        // reloading would silently destroy the user's in-progress edits.
        var key = pdk.FilePath ?? pdk.Name;
        if (_openPdkEditWindows.TryGetValue(key, out var existingWindow) && existingWindow.IsVisible)
        {
            // Un-minimize first: Activate() alone leaves a minimized window minimized,
            // which looks like the button silently did nothing.
            existingWindow.WindowState = WindowState.Normal;
            existingWindow.Activate();
            return;
        }

        var processVm = new ProcessManagementViewModel(new FileDialogService(this))
        {
            // Resolve straight to this row's own file path — no name-based lookup needed since
            // the button is already scoped to this exact PDK.
            PdkFilePathResolver = _ => pdk.FilePath,
            // Confirm before overwriting the PDK's JSON on disk (same prompt as the former
            // toolbar dialog): naming the exact file so it can't be edited by accident.
            ConfirmSaveToPdk = async path =>
            {
                var choice = await new MessageBoxService().ShowChoicePromptAsync(
                    $"This overwrites the PDK file on disk:\n{path}\n\nOnly this process's own "
                    + "layers and cross-sections are written. Continue?",
                    "Save to PDK file?", new[] { "Cancel", "Save" });
                return choice == 1;
            },
        };
        processVm.LoadForSinglePdkEdit(draft);
        // Re-apply the active process lock by value after a save, so an edit that changes this
        // PDK's fingerprint is reflected immediately without a restart.
        processVm.ProcessSaved += async (_, _) =>
        {
            vm.LeftPanel.ReapplyActiveProcessAfterPdkChange();
            await WarnIfSavedProcessDivergedFromDesign(vm, pdk);
        };

        var processWindow = new ProcessManagementWindow
        {
            DataContext = processVm,
            Title = $"Edit Process — {pdk.Name}",
        };
        _openPdkEditWindows[key] = processWindow;
        // Only remove the entry if it still points at THIS window: if the key was ever
        // re-assigned to a newer window, the older window's Closed handler must not
        // deregister the newer one.
        processWindow.Closed += (_, _) =>
        {
            if (_openPdkEditWindows.TryGetValue(key, out var tracked) && ReferenceEquals(tracked, processWindow))
                _openPdkEditWindows.Remove(key);
        };
        processWindow.Show(this);
    }

    /// <summary>
    /// Warns the user when a per-PDK process save (<see cref="PdkEditProcess_Click"/>) diverged
    /// <paramref name="pdk"/> from the design's active process (issue #570 follow-up, LC-T4):
    /// the placement lock (<c>IsLockedByProcess</c>, just recomputed by
    /// <see cref="LeftPanelViewModel.ReapplyActiveProcessAfterPdkChange"/>) already blocks NEW
    /// placements from this PDK, but components placed from it BEFORE the edit are deliberately
    /// kept on the canvas — this tells the user they are now in conflict instead of leaving that
    /// discoverable only via Design Checks. No dialog when the PDK isn't locked (no divergence)
    /// or there are zero placed components from it (the lock alone is enough). Never deletes
    /// anything.
    /// </summary>
    private static async Task WarnIfSavedProcessDivergedFromDesign(MainViewModel vm, PdkInfoViewModel pdk)
    {
        var pdkInfo = vm.LeftPanel.PdkManager.LoadedPdks.FirstOrDefault(p =>
            pdk.FilePath != null ? p.FilePath == pdk.FilePath : p.Name == pdk.Name);
        if (pdkInfo is not { IsLockedByProcess: true })
            return;

        var conflictedCount = vm.Canvas.Components.Count(c =>
            (c.TemplatePdkSource ?? vm.CanvasInteraction.ResolveComponentPdkSource?.Invoke(c.Component))
            == pdkInfo.Name);
        if (conflictedCount == 0)
            return;

        await new MessageBoxService().ShowChoicePromptAsync(
            "The saved process no longer matches the design's active process. "
            + $"{conflictedCount} placed component(s) from '{pdkInfo.Name}' are now in conflict "
            + "and new placements are blocked. Existing components are kept — see Design Checks.",
            "Process Changed", new[] { "OK" });
    }

    /// <summary>
    /// Handles "Delete…" click on a custom PDK's row in PDK Management (LC-T5): after a confirm
    /// prompt, moves the whole PDK file to <c>user-pdks/.trash</c> via <see cref="UserPdkStore"/>
    /// and then deregisters it from the library (templates, PDK-manager entry, in-memory draft,
    /// remembered import path) via <see cref="LeftPanelViewModel.UnregisterPdk"/> — mirrors
    /// <see cref="PdkCreate_Click"/>'s store-then-register order, just in reverse. Bundled PDKs
    /// have no Delete button (see the <c>!IsBundled</c> visibility binding in
    /// <c>MainWindow.axaml</c>), but the check is repeated here as the authoritative guard.
    /// Placed components on the canvas are never touched (Design Checks flag any resulting
    /// conflict, mirroring <see cref="WarnIfSavedProcessDivergedFromDesign"/>).
    /// </summary>
    private async void PdkDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PdkInfoViewModel pdk } || pdk.IsBundled || pdk.FilePath is null)
            return;
        if (DataContext is not MainViewModel vm)
            return;

        var userPdkStore = App.Services.GetService(typeof(UserPdkStore)) as UserPdkStore;
        if (userPdkStore is null)
            return;

        // Only files inside the managed user-pdks root are moved to its .trash. An
        // externally-stored PDK (imported from an arbitrary folder, remembered via preferences)
        // is the user's own file in a place they chose — deleting it from the library must not
        // relocate it into a hidden app-data folder (PR #739 review), so that path only
        // deregisters and leaves the file untouched.
        var isManaged = userPdkStore.IsInManagedRoot(pdk.FilePath);
        var prompt = isManaged
            ? $"Move '{pdk.Name}' ({pdk.ComponentCount} components) to trash?\n\n"
              + "The file is moved to user-pdks/.trash and can be restored manually."
            : $"Remove '{pdk.Name}' ({pdk.ComponentCount} components) from the library?\n\n"
              + $"The file stays untouched at:\n{pdk.FilePath}";
        var choice = await new MessageBoxService().ShowChoicePromptAsync(
            prompt, "Delete PDK?", new[] { "Cancel", isManaged ? "Move to Trash" : "Remove" });
        if (choice != 1)
            return;

        if (isManaged)
        {
            try
            {
                userPdkStore.MoveToTrash(pdk.FilePath);
            }
            catch (Exception ex)
            {
                vm.ErrorConsole.LogError($"Failed to move PDK '{pdk.Name}' to trash: {ex.Message}", ex);
                return;
            }
        }

        vm.LeftPanel.UnregisterPdk(pdk.FilePath);
    }

    /// <summary>
    /// Handles the "+" click in the PDK-Management panel header (issue #700 follow-up, LC-T2):
    /// opens <see cref="CreateCustomPdkWindow"/> directly, modal on the main window, instead of
    /// going through the "New Component" assistant's "New PDK…" sentinel first. Built the same
    /// way as that assistant's <c>CreateNewPdk</c> hook (<c>ShowNewComponentWindowAsync</c>
    /// lambda above) — same view model, same available-processes filter, same
    /// <see cref="ProcessManagementViewModel"/> definition editor — just with the main window as
    /// owner and no parent assistant window to close afterwards. On success, the (possibly
    /// component-less) new PDK is registered straight into the library via
    /// <see cref="LeftPanel.RegisterCreatedPdk"/> so it appears in the list immediately.
    /// </summary>
    private async void PdkCreate_Click(object? sender, RoutedEventArgs e)
    {
        await ShowCreateCustomPdkDialogAsync(this);
    }

    /// <summary>
    /// The single shared wiring for the Create-Custom-PDK dialog — used by both entry points
    /// (the PDK-Management "+" button above and the New Component assistant's "New PDK…"
    /// sentinel), so their importer lists, event wiring, and post-create registration can never
    /// drift apart (PR #739 review). Shows the dialog modally over <paramref name="owner"/>;
    /// on success the (possibly component-less) new PDK is registered straight into the library
    /// via <see cref="LeftPanelViewModel.RegisterCreatedPdk"/> — so it appears in PDK Management
    /// immediately regardless of entry point — and its file path is returned. Returns null when
    /// the user cancelled or the dialog faulted (logged, not swallowed).
    /// </summary>
    private async Task<string?> ShowCreateCustomPdkDialogAsync(Window owner)
    {
        if (DataContext is not MainViewModel vm)
            return null;

        var userPdkStore = App.Services.GetService(typeof(UserPdkStore)) as UserPdkStore;
        if (userPdkStore is null)
            return null;

        var availableProcesses = vm.LeftPanel.GetLoadedPdkDrafts()
            .Where(d => d.Process != null && !d.ProcessAgnostic)
            .Select(d => d.Process!)
            .ToList();
        var processDefinitionEditor = new ProcessManagementViewModel(new FileDialogService(this),
            new IProcessImporter[]
            {
                new UpdkYamlProcessImporter(),
                new NazcaCsvProcessImporter(),
            }, new PdkJsonSaver());

        var createVm = new CreateCustomPdkViewModel(userPdkStore, availableProcesses, processDefinitionEditor);
        var createWindow = new CreateCustomPdkWindow { DataContext = createVm };

        string? createdPath = null;
        createVm.PdkCreated += (_, path) =>
        {
            createdPath = path;
            createWindow.Close();
        };

        try
        {
            await createWindow.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            vm.ErrorConsole.LogError($"Create-PDK dialog failed: {ex.Message}", ex);
            return null;
        }

        if (createdPath is not null)
            vm.LeftPanel.RegisterCreatedPdk(createdPath);
        return createdPath;
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

        // Guided Docker setup (issue #649): shown when the availability probe
        // reports Docker missing or its engine stopped.
        var dockerSetupDialog = App.Services.GetService(typeof(CAP.Avalonia.Services.Solvers.IDockerSetupDialogService))
            as CAP.Avalonia.Services.Solvers.IDockerSetupDialogService;
        var notificationService = App.Services.GetService(typeof(INotificationService))
            as INotificationService;

        var dialogVm = new ComponentSettingsDialogViewModel(
            new FileDialogService(this),
            errorConsole,
            importers: null,
            portMappingDialog: portMappingDialog,
            fdtdService: fdtdService,
            fdtdRequestFactory: fdtdRequestFactory,
            notificationService: notificationService,
            dockerSetupDialog: dockerSetupDialog);

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

        // Issue #580 E: after a per-instance FDTD recompute, promote the result to
        // the user-global template override — but only while the instance geometry
        // still matches the template draft (no Nazca override active). Everything
        // is checked at invoke time so mid-session geometry edits are honoured.
        Func<CAP_DataAccess.Persistence.PIR.ComponentSMatrixData, bool>? propagateToTemplate = null;
        if (liveComponent != null && userStore != null)
        {
            propagateToTemplate = data =>
            {
                var templateKey = vm.FileOperations.ResolveTemplateKey(liveComponent);
                if (templateKey == null)
                    return false; // no matching PDK template (e.g. user group)

                vm.FileOperations.StoredNazcaOverrides
                    .TryGetValue(liveComponent.Identifier, out var nazcaOverride);
                if (!CAP.Avalonia.Services.TemplateGeometryMatch.Matches(
                        liveComponent, nazcaOverride,
                        templateModuleName, templateFunctionName, templateFunctionParameters))
                    return false;

                userStore.Overrides[templateKey] = data;
                userStore.Save();
                vm.FileOperations.ReapplyTemplateOverrides();
                vm.LeftPanel.HierarchyPanel.RefreshOverrideMarkers();
                RefreshTemplateOverrideBadges(vm);
                return true;
            };
        }

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
            propagateToTemplate: propagateToTemplate,
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
