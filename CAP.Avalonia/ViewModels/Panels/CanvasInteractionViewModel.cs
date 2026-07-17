using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.Components.PinKinds;
using CAP_Core.Components.Process;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Services;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Interaction mode for the canvas.
/// </summary>
public enum InteractionMode
{
    Select,
    PlaceComponent,
    PlaceGroupTemplate,
    Connect,
    Delete,
    Probe
}

/// <summary>
/// ViewModel for canvas interaction logic.
/// Handles user interactions: selection, placement, connection, deletion, and component manipulation.
/// Max 250 lines per CLAUDE.md guideline.
/// </summary>
public partial class CanvasInteractionViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly CommandManager _commandManager;
    private readonly ComponentLibraryViewModel? _libraryViewModel;
    private readonly GroupPreviewGenerator? _previewGenerator;
    private IInputDialogService? _inputDialogService;

    [ObservableProperty]
    private InteractionMode _currentMode = InteractionMode.Select;

    [ObservableProperty]
    private ComponentTemplate? _selectedTemplate;

    [ObservableProperty]
    private GroupTemplate? _selectedGroupTemplate;

    [ObservableProperty]
    private ComponentViewModel? _selectedComponent;

    [ObservableProperty]
    private WaveguideConnectionViewModel? _selectedWaveguideConnection;

    private PhysicalPin? _connectionStartPin;
    private double _moveStartX;
    private double _moveStartY;
    private ComponentViewModel? _movingComponent;
    private Dictionary<ComponentViewModel, (double x, double y)>? _groupMoveStartPositions;

    /// <summary>
    /// Callback to update status text in the UI.
    /// </summary>
    public Action<string>? UpdateStatus { get; set; }

    /// <summary>
    /// Callback to notify when selection changes (for syncing with hierarchy panel).
    /// </summary>
    public Action<ComponentViewModel?>? OnSelectionChanged { get; set; }

    /// <summary>
    /// Callback to clear group template selection in left panel.
    /// </summary>
    public Action? ClearLeftPanelGroupSelection { get; set; }

    /// <summary>
    /// Callback to clear component template selection in main view.
    /// </summary>
    public Action? ClearComponentTemplateSelection { get; set; }

    /// <summary>
    /// Callback for "Edit Component…" from the canvas context menu. Wired by
    /// <c>MainWindow.axaml.cs</c> to the unified "Edit Component" editor when the component's
    /// PDK template is resolvable and editable, falling back to the per-instance
    /// <c>ComponentSettingsDialog</c> otherwise (ComponentGroups, template-less instances).
    /// </summary>
    public Action<ComponentViewModel>? OpenComponentSettings { get; set; }

    /// <summary>
    /// Callback invoked when the user probes an element in Probe mode (issue #691):
    /// carries the classified probe target plus the click position in canvas coordinates.
    /// Wired by <c>MainViewModel</c> to open the mode-slice flyout at the click point.
    /// </summary>
    public Action<CAP_Core.Solvers.ModeProbe.ProbeTarget, double, double>? ProbeRequested { get; set; }

    /// <summary>
    /// Callback returning the design's active process (issue #570), consulted before
    /// placement and paste so a component from a foreign PDK is rejected. Wired by
    /// <c>MainViewModel</c> to <c>FileOperationsViewModel.ActiveProcess</c>.
    /// </summary>
    public Func<ActiveProcessSelection?>? GetActiveProcess { get; set; }

    /// <summary>
    /// Callback returning the names of loaded PDKs flagged process-agnostic (e.g. "Analysis
    /// Tools"), which stay placeable/pasteable regardless of the active process (issue #570).
    /// Wired by <c>MainViewModel</c> to <c>LeftPanelViewModel.GetProcessAgnosticPdkNames</c>.
    /// </summary>
    public Func<IReadOnlyCollection<string>>? GetProcessAgnosticPdkNames { get; set; }

    /// <summary>
    /// Callback returning the by-value-compatible member PDK names for the active process
    /// (issue placement-livemembers), computed live against the current PDK catalog rather than
    /// trusting the persisted <see cref="ActiveProcessSelection.MemberPdkNames"/> snapshot
    /// (#732). This is what allows a custom PDK registered after the process was saved — but
    /// physically the same process — to be placed/pasted. Wired by <c>MainViewModel</c> to
    /// <c>LeftPanelViewModel.ResolveLiveMemberPdkNames</c>; null when unwired falls back to the
    /// snapshot-only check.
    /// </summary>
    public Func<IReadOnlyCollection<string>?>? GetLiveMemberPdkNames { get; set; }

    /// <summary>
    /// Callback resolving the PDK source of a placed core component (groups carry none of
    /// their own, so their children are resolved individually — issue #653). Wired by
    /// <c>MainViewModel</c> to <c>ComponentPdkSourceResolver.Resolve</c> over the loaded
    /// component library. When unwired, group children resolve to null (treated as built-in).
    /// </summary>
    public Func<Component, string?>? ResolveComponentPdkSource { get; set; }

    public CanvasInteractionViewModel(
        DesignCanvasViewModel canvas,
        CommandManager commandManager,
        ComponentLibraryViewModel? libraryViewModel = null,
        GroupPreviewGenerator? previewGenerator = null,
        IInputDialogService? inputDialogService = null)
    {
        _canvas = canvas;
        _commandManager = commandManager;
        _libraryViewModel = libraryViewModel;
        _previewGenerator = previewGenerator;
        _inputDialogService = inputDialogService;

        // Hierarchy → right panel: when canvas.SelectedComponent changes externally
        // (e.g. from the hierarchy panel), mirror it so the right-panel property editor updates.
        _canvas.PropertyChanged += OnCanvasPropertyChanged;
    }

    /// <summary>
    /// Keeps <see cref="SelectedComponent"/> in sync when
    /// <see cref="DesignCanvasViewModel.SelectedComponent"/> is changed externally
    /// (e.g. by the hierarchy panel).
    /// CommunityToolkit's equality check prevents the setter from firing again when
    /// the value is already up-to-date, so there is no feedback loop.
    /// </summary>
    private void OnCanvasPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesignCanvasViewModel.SelectedComponent))
            SelectedComponent = _canvas.SelectedComponent;
    }

    partial void OnSelectedTemplateChanged(ComponentTemplate? value)
    {
        if (value != null)
        {
            CurrentMode = InteractionMode.PlaceComponent;
            SelectedGroupTemplate = null; // Deselect group template in CanvasInteraction
            ClearLeftPanelGroupSelection?.Invoke(); // Deselect group template in LeftPanel UI
            UpdateStatus?.Invoke($"Click on canvas to place: {value.Name}");
        }
    }

    partial void OnSelectedGroupTemplateChanged(GroupTemplate? value)
    {
        if (value != null)
        {
            CurrentMode = InteractionMode.PlaceGroupTemplate;
            SelectedTemplate = null; // Deselect component template in CanvasInteraction
            ClearComponentTemplateSelection?.Invoke(); // Deselect component template in UI ListBox
            UpdateStatus?.Invoke($"Click on canvas to place group: {value.Name}");
        }
    }

    partial void OnCurrentModeChanged(InteractionMode value)
    {
        _connectionStartPin = null;
        _canvas.ClearPinHighlight();

        // Deselect templates when switching away from placement modes
        if (value != InteractionMode.PlaceComponent && value != InteractionMode.PlaceGroupTemplate)
        {
            SelectedTemplate = null;
            SelectedGroupTemplate = null;
            // Clear UI selections as well
            ClearComponentTemplateSelection?.Invoke();
            ClearLeftPanelGroupSelection?.Invoke();
        }

        // Deselect canvas components when switching away from Select mode
        if (value != InteractionMode.Select)
        {
            SelectedComponent = null;
        }

        var statusText = value switch
        {
            InteractionMode.Select => "Select mode: Click to select, drag to move",
            InteractionMode.PlaceComponent when SelectedTemplate != null => $"Place mode: Click to place {SelectedTemplate.Name}",
            InteractionMode.PlaceComponent => "Place mode: Select a component from the library",
            InteractionMode.PlaceGroupTemplate when SelectedGroupTemplate != null => $"Place mode: Click to place group {SelectedGroupTemplate.Name}",
            InteractionMode.PlaceGroupTemplate => "Place mode: Select a group from Saved Groups",
            InteractionMode.Connect => "Connect mode: Move near a pin to start connection",
            InteractionMode.Delete => "Delete mode: Click on component or connection to delete",
            InteractionMode.Probe => "Probe mode: Click a waveguide or coupler to inspect its mode slice",
            _ => "Ready"
        };

        UpdateStatus?.Invoke(statusText);
    }

    partial void OnSelectedComponentChanged(ComponentViewModel? value)
    {
        // Keep canvas in sync when this property is set from outside (e.g. tests or mirroring).
        if (_canvas.SelectedComponent != value)
            _canvas.SelectedComponent = value;

        if (value?.IsLightSource == true)
        {
            var cfg = value.LaserConfig!;
            UpdateStatus?.Invoke($"Selected: {value.Name} [{cfg.WavelengthLabel}, Power={cfg.InputPower:F2}]");
        }

        OnSelectionChanged?.Invoke(value);
        OpenSelectedComponentSettingsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Handles canvas click events.
    /// </summary>
    public void CanvasClicked(double canvasX, double canvasY)
    {
        switch (CurrentMode)
        {
            case InteractionMode.PlaceComponent:
                PlaceComponentAt(canvasX, canvasY);
                break;
            case InteractionMode.PlaceGroupTemplate:
                PlaceGroupTemplateAt(canvasX, canvasY);
                break;
            case InteractionMode.Select:
                SelectAt(canvasX, canvasY);
                break;
            case InteractionMode.Connect:
                var pin = _canvas.HighlightedPin?.Pin ?? _canvas.GetPinAt(canvasX, canvasY);
                if (pin != null)
                {
                    HandlePinClickForConnection(pin);
                }
                else
                {
                    CurrentMode = InteractionMode.Select;
                    _canvas.ClearPinHighlight();
                }
                break;
            case InteractionMode.Delete:
                DeleteAt(canvasX, canvasY);
                break;
            case InteractionMode.Probe:
                ProbeAt(canvasX, canvasY);
                break;
        }
    }

    /// <summary>
    /// Handles pin click events in Connect mode.
    /// </summary>
    public void PinClicked(PhysicalPin pin)
    {
        if (CurrentMode == InteractionMode.Connect)
        {
            HandlePinClickForConnection(pin);
        }
    }

    /// <summary>
    /// Handles mouse movement on canvas (for pin highlighting in Connect mode).
    /// </summary>
    public void CanvasMouseMove(double canvasX, double canvasY)
    {
        if (CurrentMode == InteractionMode.Connect)
        {
            var nearPin = _canvas.UpdatePinHighlight(canvasX, canvasY, _connectionStartPin);

            if (nearPin != null)
            {
                var pinName = nearPin.Name;
                var compName = nearPin.ParentComponentViewModel.Name;

                if (_connectionStartPin != null)
                {
                    UpdateStatus?.Invoke($"Click to connect to {pinName} on {compName}");
                }
                else
                {
                    UpdateStatus?.Invoke($"Click {pinName} on {compName} to start connection");
                }
            }
            else if (_connectionStartPin != null)
            {
                UpdateStatus?.Invoke($"Connection started from {_connectionStartPin.Name}. Move near a pin to connect.");
            }
            else
            {
                UpdateStatus?.Invoke("Connect mode: Move near a pin to start connection");
            }
        }
        else
        {
            _canvas.ClearPinHighlight();
        }
    }

    private void HandlePinClickForConnection(PhysicalPin pin)
    {
        if (_connectionStartPin == null)
        {
            _connectionStartPin = pin;
            UpdateStatus?.Invoke($"Connection started from {pin.Name}. Click another pin to complete.");
        }
        else
        {
            if (_connectionStartPin == pin || _connectionStartPin.ParentComponent == pin.ParentComponent)
            {
                UpdateStatus?.Invoke("Cannot connect pin to itself or same component");
            }
            else if (!PinKindHelper.AreKindsCompatible(_connectionStartPin, pin))
            {
                // Cross-domain connection (optical ↔ electrical) is physically meaningless — reject.
                UpdateStatus?.Invoke(PinKindHelper.DescribeIncompatibility(_connectionStartPin, pin));
            }
            else
            {
                var cmd = new CreateConnectionCommand(_canvas, _connectionStartPin, pin);
                _commandManager.ExecuteCommand(cmd);
                UpdateStatus?.Invoke($"Connected {_connectionStartPin.Name} to {pin.Name}");
            }
            _connectionStartPin = null;
        }
    }

    private void PlaceComponentAt(double x, double y)
    {
        if (SelectedTemplate == null) return;

        var (isAllowed, blockReason) = SingleProcessPolicy.CheckPlacement(
            GetActiveProcess?.Invoke(), SelectedTemplate.PdkSource,
            GetProcessAgnosticPdkNames?.Invoke() ?? Array.Empty<string>(),
            GetLiveMemberPdkNames?.Invoke());
        if (!isAllowed)
        {
            UpdateStatus?.Invoke(blockReason ?? "Process mismatch — cannot place component.");
            return;
        }

        double centeredX = x - SelectedTemplate.WidthMicrometers / 2;
        double centeredY = y - SelectedTemplate.HeightMicrometers / 2;

        var cmd = PlaceComponentCommand.TryCreate(_canvas, SelectedTemplate, centeredX, centeredY);
        if (cmd == null)
        {
            UpdateStatus?.Invoke("No space available on chip for this component");
            return;
        }

        _commandManager.ExecuteCommand(cmd);
        UpdateStatus?.Invoke($"Placed {SelectedTemplate.Name} at ({x:F0}, {y:F0})µm");
    }

    private void PlaceGroupTemplateAt(double x, double y)
    {
        if (SelectedGroupTemplate == null || _libraryViewModel == null) return;

        // Debug: Check if TemplateGroup is loaded
        if (SelectedGroupTemplate.TemplateGroup == null)
        {
            UpdateStatus?.Invoke($"ERROR: Template '{SelectedGroupTemplate.Name}' not loaded! TemplateGroup is null.");
            return;
        }

        // Single-process enforcement over the group's children (issue #653): a group has no
        // PdkSource of its own, so a foreign-process child must not slip in via grouping.
        var (isAllowed, blockReason) = GroupProcessPolicy.CheckGroupPlacement(
            GetActiveProcess?.Invoke(),
            ChildPdkSources(SelectedGroupTemplate.TemplateGroup),
            GetProcessAgnosticPdkNames?.Invoke() ?? Array.Empty<string>(),
            GetLiveMemberPdkNames?.Invoke(),
            SelectedGroupTemplate.Name);
        if (!isAllowed)
        {
            UpdateStatus?.Invoke(blockReason ?? "Process mismatch — cannot place group.");
            return;
        }

        var libraryManager = _libraryViewModel.GetLibraryManager();
        var cmd = PlaceGroupTemplateCommand.TryCreate(_canvas, libraryManager, SelectedGroupTemplate, x, y);

        if (cmd == null)
        {
            UpdateStatus?.Invoke("No space available on chip for this group or template not loaded");
            return;
        }

        _commandManager.ExecuteCommand(cmd);
        UpdateStatus?.Invoke($"Placed group '{SelectedGroupTemplate.Name}' at ({x:F0}, {y:F0})µm");
    }

    /// <summary>
    /// Resolved PDK source of every recursive non-group child of <paramref name="group"/>,
    /// used to check the single-process policy over a group's contents (issue #653).
    /// </summary>
    private IEnumerable<string?> ChildPdkSources(ComponentGroup group) =>
        group.GetAllComponentsRecursive()
            .Where(child => child is not ComponentGroup)
            .Select(child => ResolveComponentPdkSource?.Invoke(child));

    /// <summary>
    /// Selects the component or connection at the given canvas position, keeping the
    /// <see cref="DesignCanvasViewModel.Selection"/> set and <see cref="SelectedComponent"/> in sync.
    /// Invoked by the canvas right-click handler so the context menu acts on the element under the
    /// cursor rather than the previously selected one.
    /// </summary>
    public void SelectComponentAt(double canvasX, double canvasY)
    {
        var hit = ComponentAt(canvasX, canvasY);

        // Right-clicking one of several already-selected components keeps the whole
        // multi-selection (so "Create Group" stays available) and just makes the
        // clicked one the primary for the context menu / component settings.
        // Right-clicking outside the selection (or with only one selected) selects
        // just that component, exactly like a left-click would.
        if (hit != null && _canvas.Selection.HasMultipleSelected && _canvas.Selection.SelectedComponents.Contains(hit))
        {
            SelectedComponent = hit;
            _canvas.SelectedComponent = hit;
            SelectedWaveguideConnection = null;
            UpdateStatus?.Invoke($"Selected: {hit.Name} ({_canvas.Selection.SelectedComponents.Count} selected)");
            return;
        }

        SelectAt(canvasX, canvasY);
        if (SelectedComponent != null)
            _canvas.Selection.SelectSingle(SelectedComponent);
        else
            _canvas.Selection.ClearSelection();
    }

    /// <summary>Returns the topmost component whose bounds contain the point, or null.</summary>
    private ComponentViewModel? ComponentAt(double x, double y) =>
        _canvas.Components
            .Where(c => x >= c.X && x <= c.X + c.Width && y >= c.Y && y <= c.Y + c.Height)
            .LastOrDefault();

    private void SelectAt(double x, double y)
    {
        // Deselect all
        foreach (var comp in _canvas.Components)
        {
            comp.IsSelected = false;
        }
        foreach (var conn in _canvas.Connections)
        {
            conn.IsSelected = false;
        }

        // Find component at position
        var component = ComponentAt(x, y);

        if (component != null)
        {
            component.IsSelected = true;
            SelectedComponent = component;
            _canvas.SelectedComponent = component;
            SelectedWaveguideConnection = null;
            UpdateStatus?.Invoke($"Selected: {component.Name}");
        }
        else
        {
            var connection = FindConnectionAt(x, y);
            if (connection != null)
            {
                connection.IsSelected = true;
                SelectedWaveguideConnection = connection;
                SelectedComponent = null;
                _canvas.SelectedComponent = null;
                UpdateStatus?.Invoke($"Selected connection: {connection.PathLength:F1}µm, Loss: {connection.LossDb:F2}dB");
            }
            else
            {
                SelectedComponent = null;
                _canvas.SelectedComponent = null;
                SelectedWaveguideConnection = null;
            }
        }
    }

    private void DeleteAt(double x, double y)
    {
        var component = _canvas.Components
            .Where(c => x >= c.X && x <= c.X + c.Width && y >= c.Y && y <= c.Y + c.Height)
            .LastOrDefault();

        if (component != null)
        {
            var name = component.Name;
            var cmd = new DeleteComponentCommand(_canvas, component);
            _commandManager.ExecuteCommand(cmd);
            SelectedComponent = null;
            UpdateStatus?.Invoke($"Deleted: {name}");
            return;
        }

        var connection = FindConnectionAt(x, y);
        if (connection != null)
        {
            var cmd = new DeleteConnectionCommand(_canvas, connection);
            _commandManager.ExecuteCommand(cmd);
            UpdateStatus?.Invoke("Deleted connection");
        }
    }

    /// <summary>
    /// Probes the element at the given canvas position (issue #691): a clicked waveguide
    /// connection carries its own width; a clicked component is classified as fiber
    /// coupler / interference region and borrows the width of an attached connection.
    /// Raises <see cref="ProbeRequested"/> so the host opens the mode-slice flyout.
    /// </summary>
    private void ProbeAt(double x, double y)
    {
        var component = ComponentAt(x, y);
        if (component != null)
        {
            var attachedWidth = _canvas.Connections
                .Where(c => c.Connection.StartPin.ParentComponent == component.Component
                         || c.Connection.EndPin.ParentComponent == component.Component)
                .Select(c => (double?)c.Connection.WidthMicrometers)
                .FirstOrDefault();
            var target = CAP_Core.Solvers.ModeProbe.ProbeTarget.ForComponent(component.Name, attachedWidth);
            ProbeRequested?.Invoke(target, x, y);
            return;
        }

        var connection = FindConnectionAt(x, y);
        if (connection != null)
        {
            var target = CAP_Core.Solvers.ModeProbe.ProbeTarget.ForConnection(
                connection.Connection.WidthMicrometers, connection.PathLength);
            ProbeRequested?.Invoke(target, x, y);
            return;
        }

        UpdateStatus?.Invoke("Probe mode: Click a waveguide or coupler to inspect its mode slice");
    }

    private WaveguideConnectionViewModel? FindConnectionAt(double x, double y)
    {
        const double hitTolerance = 10.0;

        // Hit-test the ACTUAL routed path (its segments), not the straight endpoint
        // line — otherwise a bent/L-shaped route can't be clicked where it's drawn.
        // Pick the closest connection within tolerance so overlapping paths resolve
        // to the one nearest the cursor.
        WaveguideConnectionViewModel? closest = null;
        var closestDistance = hitTolerance;
        foreach (var conn in _canvas.Connections)
        {
            var distance = DistanceToConnectionPath(conn, x, y);
            if (distance <= closestDistance)
            {
                closestDistance = distance;
                closest = conn;
            }
        }
        return closest;
    }

    /// <summary>
    /// Shortest distance from a canvas point to a connection's drawn path: the minimum
    /// over its routed segments (arcs approximated by their chord — fine at the 10 px
    /// hit tolerance), or the straight endpoint line when the connection isn't routed yet.
    /// </summary>
    private static double DistanceToConnectionPath(WaveguideConnectionViewModel conn, double x, double y)
    {
        var segments = conn.Connection.GetPathSegments();
        if (segments.Count == 0)
            return PointToLineDistance(x, y, conn.StartX, conn.StartY, conn.EndX, conn.EndY);

        var min = double.MaxValue;
        foreach (var seg in segments)
        {
            var d = PointToLineDistance(
                x, y, seg.StartPoint.X, seg.StartPoint.Y, seg.EndPoint.X, seg.EndPoint.Y);
            if (d < min) min = d;
        }
        return min;
    }

    private static double PointToLineDistance(double px, double py, double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var lengthSq = dx * dx + dy * dy;

        if (lengthSq < 0.0001)
        {
            return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
        }

        var t = Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / lengthSq));
        var projX = x1 + t * dx;
        var projY = y1 + t * dy;

        return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }

    /// <summary>
    /// Starts dragging a component.
    /// </summary>
    public void StartMoveComponent(ComponentViewModel component)
    {
        _movingComponent = component;
        _moveStartX = component.X;
        _moveStartY = component.Y;
        _canvas.BeginDragComponent(component);
    }

    /// <summary>
    /// Starts dragging multiple components as a group.
    /// </summary>
    public void StartGroupMove(IEnumerable<ComponentViewModel> components)
    {
        _groupMoveStartPositions = new Dictionary<ComponentViewModel, (double x, double y)>();
        foreach (var comp in components)
        {
            _groupMoveStartPositions[comp] = (comp.X, comp.Y);
        }

        var firstComp = components.FirstOrDefault();
        if (firstComp != null)
        {
            _canvas.BeginDragComponent(firstComp);
        }
    }

    /// <summary>
    /// Ends dragging a component and creates undo command.
    /// </summary>
    public void EndMoveComponent()
    {
        if (_movingComponent != null)
        {
            _canvas.EndDragComponent(_movingComponent);

            if (Math.Abs(_movingComponent.X - _moveStartX) > 0.001 ||
                Math.Abs(_movingComponent.Y - _moveStartY) > 0.001)
            {
                var cmd = new MoveComponentCommand(
                    _canvas,
                    _movingComponent,
                    _moveStartX,
                    _moveStartY,
                    _movingComponent.X,
                    _movingComponent.Y);
                _commandManager.ExecuteCommand(cmd);
            }
        }
        _movingComponent = null;
    }

    /// <summary>
    /// Ends dragging multiple components and creates undo command.
    /// </summary>
    public void EndGroupMove(IEnumerable<ComponentViewModel> components)
    {
        if (_groupMoveStartPositions == null || !_groupMoveStartPositions.Any())
            return;

        var firstComp = _groupMoveStartPositions.Keys.FirstOrDefault();
        if (firstComp == null)
            return;

        _canvas.EndDragComponent(firstComp);

        var startPos = _groupMoveStartPositions[firstComp];
        double deltaX = firstComp.X - startPos.x;
        double deltaY = firstComp.Y - startPos.y;

        if (Math.Abs(deltaX) > 0.001 || Math.Abs(deltaY) > 0.001)
        {
            var cmd = new GroupMoveCommand(
                _canvas,
                _groupMoveStartPositions.Keys.ToList(),
                deltaX,
                deltaY);
            _commandManager.ExecuteCommand(cmd);
        }

        _groupMoveStartPositions = null;
    }

    [RelayCommand]
    private void SetSelectMode()
    {
        CurrentMode = InteractionMode.Select;
        SelectedTemplate = null;
        SelectedGroupTemplate = null;
        _connectionStartPin = null;
    }

    [RelayCommand]
    private void SetConnectMode()
    {
        CurrentMode = InteractionMode.Connect;
        SelectedTemplate = null;
        SelectedGroupTemplate = null;
        _connectionStartPin = null;
    }

    [RelayCommand]
    private void SetProbeMode()
    {
        CurrentMode = InteractionMode.Probe;
        SelectedTemplate = null;
        SelectedGroupTemplate = null;
        _connectionStartPin = null;
    }

    [RelayCommand]
    private void SetDeleteMode()
    {
        CurrentMode = InteractionMode.Delete;
        SelectedTemplate = null;
        SelectedGroupTemplate = null;
        _connectionStartPin = null;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var selection = _canvas.Selection;

        // The selection set is authoritative (box selection populates only the set);
        // fall back to the primary SelectedComponent when the set is empty.
        var targets = selection.SelectedComponents.ToList();
        if (targets.Count == 0 && SelectedComponent != null)
            targets.Add(SelectedComponent);

        var deletable = targets.Where(c => !c.Component.IsLocked).ToList();
        if (deletable.Count == 0)
        {
            if (targets.Count > 0)
                UpdateStatus?.Invoke("Selection is locked — unlock elements to delete them");
            return;
        }

        // One batch command for a multi-selection, so a single undo restores everything.
        IUndoableCommand cmd = deletable.Count == 1
            ? new DeleteComponentCommand(_canvas, deletable[0])
            : new GroupDeleteCommand(_canvas, deletable);
        _commandManager.ExecuteCommand(cmd);

        selection.ClearSelection();
        SelectedComponent = null;
        UpdateStatus?.Invoke(deletable.Count == 1
            ? $"Deleted: {deletable[0].Name}"
            : $"Deleted {deletable.Count} components");
    }

    [RelayCommand]
    private void CopySelected()
    {
        var selection = _canvas.Selection;
        if (!selection.HasSelection) return;

        _canvas.Clipboard.Copy(
            selection.SelectedComponents.ToList(),
            _canvas.Connections);

        UpdateStatus?.Invoke($"Copied {selection.SelectedComponents.Count} component(s)");
    }

    /// <summary>
    /// Pastes components from clipboard at the specified position.
    /// </summary>
    public void PasteSelected(double? targetX = null, double? targetY = null)
    {
        if (!_canvas.Clipboard.HasContent) return;

        var active = GetActiveProcess?.Invoke();
        var agnosticPdkNames = GetProcessAgnosticPdkNames?.Invoke() ?? Array.Empty<string>();
        var liveMemberPdkNames = GetLiveMemberPdkNames?.Invoke();
        // PeekPdkSources expands groups to their resolved children (the clipboard's
        // PdkSourceResolver is wired by MainViewModel), so a copied group cannot
        // smuggle foreign-process components past the paste guard (issue #653).
        var blockedCount = _canvas.Clipboard.PeekPdkSources()
            .Count(pdk => !SingleProcessPolicy.CheckPlacement(active, pdk, agnosticPdkNames, liveMemberPdkNames).IsAllowed);
        if (blockedCount > 0)
        {
            UpdateStatus?.Invoke(
                $"Clipboard has {blockedCount} component(s) from another process; " +
                $"cannot paste into the '{active!.DisplayName}' design.");
            return;
        }

        var cmd = new PasteComponentsCommand(_canvas, _canvas.Clipboard, targetX, targetY);
        _commandManager.ExecuteCommand(cmd);

        if (cmd.Result != null)
        {
            _canvas.Selection.ClearSelection();
            foreach (var comp in cmd.Result.Components)
            {
                comp.IsSelected = true;
                _canvas.Selection.SelectedComponents.Add(comp);
            }

            _ = _canvas.RecalculateRoutesAsync();
            UpdateStatus?.Invoke($"Pasted {cmd.Result.Components.Count} component(s)");
        }
    }

    [RelayCommand]
    private void PasteSelectedCommand()
    {
        PasteSelected();
    }

    [RelayCommand]
    private void RotateSelected()
    {
        if (SelectedComponent != null)
        {
            var cmd = new RotateComponentCommand(_canvas, SelectedComponent);
            _commandManager.ExecuteCommand(cmd);
            UpdateStatus?.Invoke(cmd.WasApplied
                ? $"Rotated: {SelectedComponent.Name}"
                : $"Cannot rotate: {SelectedComponent.Name} would overlap another component");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateGroup))]
    private void CreateGroup()
    {
        var selectedComponents = _canvas.Selection.SelectedComponents.ToList();
        var cmd = new CreateGroupCommand(_canvas, selectedComponents);
        _commandManager.ExecuteCommand(cmd);
        _canvas.Selection.ClearSelection();

        if (_libraryViewModel != null)
        {
            UpdateStatus?.Invoke($"✓ Created group from {selectedComponents.Count} components and saved to 'Saved Groups' library");
        }
        else
        {
            UpdateStatus?.Invoke($"Created group from {selectedComponents.Count} components (not saved to library)");
        }
    }

    private bool CanCreateGroup()
    {
        return _canvas.Selection.SelectedComponents.Count >= 2;
    }

    [RelayCommand(CanExecute = nameof(CanUngroup))]
    private void Ungroup()
    {
        var selectedGroup = _canvas.Selection.SelectedComponents
            .Select(c => c.Component)
            .OfType<CAP_Core.Components.Core.ComponentGroup>()
            .FirstOrDefault();

        if (selectedGroup != null)
        {
            var cmd = new UngroupCommand(_canvas, selectedGroup);
            _commandManager.ExecuteCommand(cmd);
            _canvas.Selection.ClearSelection();
            UpdateStatus?.Invoke($"Ungrouped: {selectedGroup.GroupName}");
        }
    }

    private bool CanUngroup()
    {
        return _canvas.Selection.SelectedComponents.Count == 1 &&
               _canvas.Selection.SelectedComponents.First().Component is CAP_Core.Components.Core.ComponentGroup;
    }

    [RelayCommand(CanExecute = nameof(CanRenameGroup))]
    private async Task RenameGroup()
    {
        if (_inputDialogService == null || _libraryViewModel == null)
        {
            UpdateStatus?.Invoke("Rename not available (dialog service not configured)");
            return;
        }

        var selectedGroup = _canvas.Selection.SelectedComponents
            .Select(c => c.Component)
            .OfType<CAP_Core.Components.Core.ComponentGroup>()
            .FirstOrDefault();

        if (selectedGroup == null)
            return;

        var currentName = selectedGroup.GroupName;
        var currentDescription = selectedGroup.Description ?? "";

        var result = await _inputDialogService.ShowMultiInputDialogAsync(
            "Rename Group",
            ("Name", currentName),
            ("Description (optional)", currentDescription));

        if (result == null)
            return;

        var newName = result["Name"].Trim();
        var newDescription = result["Description (optional)"].Trim();

        if (string.IsNullOrWhiteSpace(newName))
        {
            UpdateStatus?.Invoke("Group name cannot be empty");
            return;
        }

        var cmd = new RenameGroupCommand(
            selectedGroup,
            _libraryViewModel,
            newName,
            string.IsNullOrWhiteSpace(newDescription) ? null : newDescription);

        _commandManager.ExecuteCommand(cmd);
        UpdateStatus?.Invoke($"Renamed group to '{newName}' and updated library");
    }

    private bool CanRenameGroup()
    {
        return _canvas.Selection.SelectedComponents.Count == 1 &&
               _canvas.Selection.SelectedComponents.First().Component is CAP_Core.Components.Core.ComponentGroup &&
               _libraryViewModel != null &&
               _inputDialogService != null;
    }

    [RelayCommand(CanExecute = nameof(CanSaveGroupAs))]
    private async Task SaveGroupAs()
    {
        if (_inputDialogService == null || _libraryViewModel == null)
        {
            UpdateStatus?.Invoke("Save not available (dialog service not configured)");
            return;
        }

        var selectedGroup = _canvas.Selection.SelectedComponents
            .Select(c => c.Component)
            .OfType<CAP_Core.Components.Core.ComponentGroup>()
            .FirstOrDefault();

        if (selectedGroup == null)
            return;

        var currentName = selectedGroup.GroupName;
        var currentDescription = selectedGroup.Description ?? "";

        var result = await _inputDialogService.ShowMultiInputDialogAsync(
            "Save Group as Prefab",
            ("Name", currentName),
            ("Description (optional)", currentDescription));

        if (result == null)
            return;

        var newName = result["Name"].Trim();
        var newDescription = result["Description (optional)"].Trim();

        if (string.IsNullOrWhiteSpace(newName))
        {
            UpdateStatus?.Invoke("Group name cannot be empty");
            return;
        }

        var cmd = new SaveGroupAsPrefabCommand(
            _libraryViewModel,
            _previewGenerator ?? new GroupPreviewGenerator(),
            selectedGroup,
            newName,
            string.IsNullOrWhiteSpace(newDescription) ? null : newDescription);

        _commandManager.ExecuteCommand(cmd);
        UpdateStatus?.Invoke($"Saved group '{newName}' as prefab to library");
    }

    private bool CanSaveGroupAs()
    {
        return _canvas.Selection.SelectedComponents.Count == 1 &&
               _canvas.Selection.SelectedComponents.First().Component is CAP_Core.Components.Core.ComponentGroup &&
               _libraryViewModel != null &&
               _inputDialogService != null;
    }

    /// <summary>
    /// Opens the unified "Edit Component" editor for the currently selected canvas component's
    /// PDK template, or the per-instance Component Settings dialog when no editable template
    /// resolves (e.g. ComponentGroups). Only enabled when a component is selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenSelectedComponentSettings))]
    private void OpenSelectedComponentSettings()
    {
        var selected = SelectedComponent;
        if (selected != null)
            OpenComponentSettings?.Invoke(selected);
    }

    private bool CanOpenSelectedComponentSettings()
        => SelectedComponent != null;
}
