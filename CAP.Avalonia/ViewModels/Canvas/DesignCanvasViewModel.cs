using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using CAP.Avalonia.Selection;
using CAP.Avalonia.Visualization;
using CAP.Avalonia.ViewModels.Canvas.Services;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// Thin orchestrator for the design canvas. Delegates to specialized services
/// for routing, placement, group editing, simulation, and pin highlighting.
/// </summary>
public partial class DesignCanvasViewModel : ObservableObject
{
    // ── Collections (bound by AXAML) ──────────────────────────────────────
    public ObservableCollection<ComponentViewModel> Components { get; } = new();
    public ObservableCollection<WaveguideConnectionViewModel> Connections { get; } = new();
    public ObservableCollection<PinViewModel> AllPins { get; } = new();

    /// <summary>
    /// Pin-less frozen waveguide paths living directly on the canvas (issue #856).
    /// Populated when ungrouping releases GDS-imported route geometry that has no
    /// pins to re-expand into a live connection. Rendered and persisted like
    /// group-internal paths; never simulated.
    /// </summary>
    public ObservableCollection<CanvasFrozenPathViewModel> CanvasFrozenPaths { get; } = new();

    // ── Core dependencies ─────────────────────────────────────────────────
    public WaveguideConnectionManager ConnectionManager { get; }
    public WaveguideRouter Router { get; }

    // ── Extracted services ────────────────────────────────────────────────
    public GroupEditService Groups { get; }
    public RoutingOrchestrator Routing { get; }
    public ComponentPlacementService Placement { get; }
    public SimulationCoordinator Simulation { get; }
    public PinHighlightService PinHighlight { get; }

    // ── UI services (pre-existing) ────────────────────────────────────────
    public SelectionManager Selection { get; } = new();

    /// <summary>
    /// Design-wide designation of THE output coupler evaluated by the Eye/BER and
    /// Transient analyses (issue #754). Single source of truth for both tabs;
    /// persisted with the design file.
    /// </summary>
    public AnalysisOutputDesignation AnalysisOutput { get; } = new();

    public ComponentClipboard Clipboard { get; } = new();
    public PowerFlowVisualizer PowerFlowVisualizer { get; } = new();
    public AlignmentGuideViewModel AlignmentGuide { get; } = new();
    public GridSnapSettings GridSnap { get; } = new();

    // ── Observable properties (for AXAML bindings) ────────────────────────
    [ObservableProperty] private bool _showPowerFlow;

    /// <summary>
    /// True while the Transient simulation mode is selected (#690). Transient mode
    /// clears the CW <see cref="ShowPowerFlow"/> overlay, so the canvas needs this
    /// separate flag to keep laser on/off icons visible and clickable during the
    /// transient/eye workflow. Set by MainViewModel when the mode selector changes.
    /// </summary>
    [ObservableProperty] private bool _isTransientModeActive;

    /// <summary>
    /// True when any simulation mode is active on the canvas (CW power-flow overlay
    /// or Transient mode). Used as the visibility condition for laser on/off icons.
    /// </summary>
    public bool IsSimulationModeActive => ShowPowerFlow || IsTransientModeActive;
    [ObservableProperty] private bool _useAStarRouting = true;
    [ObservableProperty] private bool _useDiagonalRouting;

    /// <summary>
    /// Routing policy (issue #860): try the direct styled geometry (straight / S-bend /
    /// sine / cobra) first; A* runs only when obstacles block the styled path. On by
    /// default; Settings → Routing offers the classic grid-first behavior as fallback.
    /// </summary>
    [ObservableProperty] private bool _preferDirectStyledRoutes = true;
    [ObservableProperty] private bool _showGridOverlay;
    [ObservableProperty] private double _minBendRadiusMicrometers = 10.0;
    [ObservableProperty] private ComponentViewModel? _selectedComponent;

    /// <summary>
    /// Keeps the <see cref="Selection"/> set consistent with the primary selection. Selecting a
    /// component that is already part of the current multi-selection (e.g. right-clicking one of
    /// several box-selected components) keeps the set intact.
    /// </summary>
    partial void OnSelectedComponentChanged(ComponentViewModel? value)
    {
        if (value == null)
        {
            // Deselecting the primary must also clear the multi-selection set — otherwise DEL
            // acts on a stale selection and deletes components that appear deselected.
            Selection.ClearSelection();
            return;
        }
        if (!Selection.SelectedComponents.Contains(value))
            Selection.SelectSingle(value);
    }

    [ObservableProperty] private double _panX;
    [ObservableProperty] private double _panY;
    [ObservableProperty] private bool _isRouting;
    [ObservableProperty] private string _routingStatusText = "";
    [ObservableProperty] private ComponentGroup? _currentEditGroup;

    // ── Callbacks ─────────────────────────────────────────────────────────
    public Action? SimulationRequested
    {
        get => Simulation.SimulationRequested;
        set => Simulation.SimulationRequested = value;
    }

    public Action? RepaintRequested
    {
        get => Routing.RepaintRequested;
        set => Routing.RepaintRequested = value;
    }

    // ── Delegated read-only properties ────────────────────────────────────
    public bool IsInGroupEditMode => Groups.IsInGroupEditMode;
    public ObservableCollection<ComponentGroup> BreadcrumbPath => Groups.BreadcrumbPath;
    public double PinHighlightDistance
    {
        get => PinHighlight.PinHighlightDistance;
        set => PinHighlight.PinHighlightDistance = value;
    }
    public PinViewModel? HighlightedPin => PinHighlight.HighlightedPin;
    public double ChipMinX { get => Placement.ChipMinX; set => Placement.ChipMinX = value; }
    public double ChipMinY { get => Placement.ChipMinY; set => Placement.ChipMinY = value; }
    public double ChipMaxX { get => Placement.ChipMaxX; set => Placement.ChipMaxX = value; }
    public double ChipMaxY { get => Placement.ChipMaxY; set => Placement.ChipMaxY = value; }

    // ── Constructors ──────────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new instance with a fresh <see cref="WaveguideRouter"/>.
    /// </summary>
    public DesignCanvasViewModel() : this(new WaveguideRouter()) { }

    /// <summary>
    /// Initializes a new instance with an injected router.
    /// </summary>
    public DesignCanvasViewModel(WaveguideRouter router)
    {
        Router = router;
        ConnectionManager = new WaveguideConnectionManager(router);

        Routing = new RoutingOrchestrator(router, ConnectionManager, Components, Connections);
        Placement = new ComponentPlacementService(Components, Connections);
        Simulation = new SimulationCoordinator(PowerFlowVisualizer);
        PinHighlight = new PinHighlightService(AllPins, GetConnectionForPin);
        Groups = new GroupEditService(
            Components, Connections, AllPins, ConnectionManager, router,
            (comp, tpl, pdk) => AddComponent(comp, tpl, pdk),
            BeginCommandExecution, EndCommandExecution,
            () => Routing.InitializeAStarRouting(),
            () => Routing.RecalculateRoutesAsync());

        // Wire service events to observable property updates
        Routing.StateChanged += () =>
        {
            IsRouting = Routing.IsRouting;
            RoutingStatusText = Routing.RoutingStatusText;
        };
        // Pre-change: update VM property BEFORE collections are modified
        // (HierarchyPanelViewModel.RebuildTree reads _canvas.CurrentEditGroup on CollectionChanged)
        Groups.CurrentEditGroupChanging += group =>
        {
            CurrentEditGroup = group;
            OnPropertyChanged(nameof(IsInGroupEditMode));
        };
        Groups.EditStateChanged += () =>
        {
            CurrentEditGroup = Groups.CurrentEditGroup;
            OnPropertyChanged(nameof(IsInGroupEditMode));
        };
        PinHighlight.HighlightChanged += () => OnPropertyChanged(nameof(HighlightedPin));
        Simulation.ShowPowerFlowChanged += (value, forceNotify) =>
        {
            if (forceNotify && ShowPowerFlow == value)
                OnPropertyChanged(nameof(ShowPowerFlow));
            else
                ShowPowerFlow = value;
        };

        // Product default: diagonal routing is opt-in (Settings → Routing);
        // the library-level router default is true, so sync it explicitly.
        Router.UseDiagonalRouting = UseDiagonalRouting;
        Router.PreferDirectStyledRoutes = PreferDirectStyledRoutes;

        Routing.InitializeAStarRouting();
    }

    // ── Property change handlers ──────────────────────────────────────────

    partial void OnMinBendRadiusMicrometersChanged(double value)
    {
        Router.MinBendRadiusMicrometers = value;
        _ = RecalculateRoutesAsync();
    }

    partial void OnUseAStarRoutingChanged(bool value) => _ = RecalculateRoutesAsync();

    partial void OnShowPowerFlowChanged(bool value) => OnPropertyChanged(nameof(IsSimulationModeActive));

    partial void OnIsTransientModeActiveChanged(bool value) => OnPropertyChanged(nameof(IsSimulationModeActive));

    partial void OnUseDiagonalRoutingChanged(bool value)
    {
        Router.UseDiagonalRouting = value;
        _ = RecalculateRoutesAsync();
    }

    partial void OnPreferDirectStyledRoutesChanged(bool value)
    {
        Router.PreferDirectStyledRoutes = value;
        _ = RecalculateRoutesAsync();
    }

    // ── Simulation delegation ─────────────────────────────────────────────

    public void InvalidateSimulation() => Simulation.InvalidateSimulation(ShowPowerFlow);
    public void RequestResimulation() => Simulation.RequestResimulation(ShowPowerFlow);
    public void RefreshPowerFlowDisplay() => Simulation.RefreshPowerFlowDisplay(ShowPowerFlow);

    // ── Routing delegation ────────────────────────────────────────────────

    public void InitializeAStarRouting() => Routing.InitializeAStarRouting();
    public void InitializeAStarRouting(double minX, double minY, double maxX, double maxY)
        => Routing.InitializeAStarRouting(minX, minY, maxX, maxY);
    public Task RecalculateRoutesAsync() => Routing.RecalculateRoutesAsync();

    // ── Drag / command execution ──────────────────────────────────────────

    public void BeginDragComponent(ComponentViewModel component) => Placement.BeginDrag(component.Component);
    public void BeginCommandExecution() => Placement.IsExecutingCommand = true;
    public void EndCommandExecution() => Placement.IsExecutingCommand = false;

    public async void EndDragComponent(ComponentViewModel component)
    {
        Placement.EndDrag();
        await RecalculateRoutesAsync();
        InvalidateSimulation();
    }

    // ── Movement / placement delegation ───────────────────────────────────

    public bool CanMoveComponentTo(ComponentViewModel component, double x, double y)
        => Placement.CanMoveComponentTo(component, x, y);

    public void MoveComponent(ComponentViewModel component, double deltaX, double deltaY)
        => Placement.MoveComponent(component, deltaX, deltaY,
            Groups.IsInGroupEditMode, Groups.CurrentEditGroup,
            Groups.UpdateExternalPinPositions, Routing.RecalculateRoutesAsync);

    /// <summary>
    /// Shifts the routed geometry of connections internal to <paramref name="selection"/> by
    /// the given delta, so waveguides follow a live joint drag of both their components.
    /// </summary>
    public void TranslateInternalConnectionRoutes(
        IEnumerable<ComponentViewModel> selection, double deltaX, double deltaY)
        => Placement.TranslateInternalConnectionRoutes(selection, deltaX, deltaY);

    public bool CanPlaceComponent(double x, double y, double width, double height,
        ComponentViewModel? excludeComponent = null)
        => Placement.CanPlaceComponent(x, y, width, height, excludeComponent);

    public (double x, double y)? FindValidPlacement(double x, double y, double width, double height)
        => Placement.FindValidPlacement(x, y, width, height);

    // ── Group editing delegation ──────────────────────────────────────────

    public void EnterGroupEditMode(ComponentGroup group) => Groups.EnterGroupEditMode(group);
    public void ExitGroupEditMode() => Groups.ExitGroupEditMode();
    [RelayCommand] public void ExitToRoot() => Groups.ExitToRoot();
    [RelayCommand] public void NavigateToBreadcrumbLevel(ComponentGroup? group)
        => Groups.NavigateToBreadcrumbLevel(group);

    // ── Pin highlight delegation ──────────────────────────────────────────

    public PinViewModel? UpdatePinHighlight(double x, double y, PhysicalPin? excludePin = null)
        => PinHighlight.UpdatePinHighlight(x, y, excludePin);
    public void ClearPinHighlight() => PinHighlight.ClearPinHighlight();
    public PhysicalPin? GetPinAt(double x, double y, double tolerance = 15.0)
        => PinHighlight.GetPinAt(x, y, tolerance);

    // ── Component lifecycle ───────────────────────────────────────────────

    public ComponentViewModel AddComponent(Component component,
        string? templateName = null, string? templatePdkSource = null)
    {
        var vm = new ComponentViewModel(component, templateName, templatePdkSource);
        vm.OnSliderChanged = () => RequestResimulation();
        if (vm.LaserConfig != null)
        {
            // Laser changes (on/off #690, wavelength, power) re-simulate the live
            // overlay and repaint the canvas laser icon.
            vm.LaserConfig.PropertyChanged += (_, e) =>
            {
                // InputPower streams continuously from the panel slider while the CW
                // overlay ignores it — only role/wavelength changes need work here.
                if (e.PropertyName == nameof(ViewModels.Simulation.LaserConfig.InputPower))
                    return;
                RequestResimulation();
                RepaintRequested?.Invoke();
            };
        }
        Components.Add(vm);
        Router.AddComponentObstacle(component);

        foreach (var pin in component.PhysicalPins)
            AllPins.Add(new PinViewModel(pin, vm));

        if (component is ComponentGroup group)
        {
            foreach (var groupPin in group.ExternalPins)
                AllPins.Add(new PinViewModel(groupPin.InternalPin, vm));
        }

        return vm;
    }

    public void RemoveComponent(ComponentViewModel component)
    {
        Router.RemoveComponentObstacle(component.Component);
        ConnectionManager.RemoveConnectionsForComponent(component.Component);

        var pinsToRemove = AllPins.Where(p => p.ParentComponentViewModel == component).ToList();
        foreach (var pin in pinsToRemove) AllPins.Remove(pin);

        var connectionsToRemove = Connections
            .Where(c => c.Connection.StartPin.ParentComponent == component.Component ||
                        c.Connection.EndPin.ParentComponent == component.Component).ToList();
        foreach (var conn in connectionsToRemove) Connections.Remove(conn);

        Components.Remove(component);
        if (ConnectionManager.Connections.Count > 0) _ = RecalculateRoutesAsync();
        InvalidateSimulation();
    }

    // ── Connection management ─────────────────────────────────────────────

    public async Task<WaveguideConnectionViewModel?> ConnectPinsAsync(
        PhysicalPin startPin, PhysicalPin endPin)
    {
        RemoveConnectionsForPin(startPin);
        RemoveConnectionsForPin(endPin);
        var connection = ConnectionManager.AddConnectionDeferred(startPin, endPin);
        var vm = new WaveguideConnectionViewModel(connection);
        Connections.Add(vm);
        await RecalculateRoutesAsync();
        InvalidateSimulation();
        return vm;
    }

    public WaveguideConnectionViewModel? ConnectPins(PhysicalPin startPin, PhysicalPin endPin)
    {
        RemoveConnectionsForPin(startPin);
        RemoveConnectionsForPin(endPin);
        var connection = ConnectionManager.AddConnectionDeferred(startPin, endPin);
        var vm = new WaveguideConnectionViewModel(connection);
        Connections.Add(vm);
        InvalidateSimulation();
        return vm;
    }

    public WaveguideConnectionViewModel? ConnectPinsWithCachedRoute(
        PhysicalPin startPin, PhysicalPin endPin, RoutedPath cachedRoute)
    {
        RemoveConnectionsForPin(startPin);
        RemoveConnectionsForPin(endPin);
        var connection = ConnectionManager.AddConnectionWithCachedRoute(startPin, endPin, cachedRoute);
        var vm = new WaveguideConnectionViewModel(connection);
        Connections.Add(vm);
        return vm;
    }

    public void RemoveConnectionsForPin(PhysicalPin pin)
    {
        var connectionsToRemove = Connections
            .Where(c => c.Connection.StartPin == pin || c.Connection.EndPin == pin).ToList();
        foreach (var conn in connectionsToRemove)
        {
            ConnectionManager.RemoveConnectionDeferred(conn.Connection);
            Connections.Remove(conn);
        }
        if (connectionsToRemove.Count > 0) InvalidateSimulation();
    }

public WaveguideConnectionViewModel? GetConnectionForPin(PhysicalPin pin)
    => Connections.FirstOrDefault(c =>
        c.Connection.StartPin == pin || c.Connection.EndPin == pin);

    /// <summary>
    /// Re-anchors or drops waveguide connections after <paramref name="component"/>'s
    /// physical pins were replaced by a per-instance Nazca override (issue #561),
    /// refreshes the canvas pin view-models, re-routes and invalidates the simulation.
    /// Returns user-facing warnings for connections that had to be dropped.
    /// </summary>
    public IReadOnlyList<string> OnComponentPinsChanged(Component component)
    {
        var result = ConnectionPinReanchorService.Reanchor(
            component, ConnectionManager.Connections);

        foreach (var droppedConn in result.DroppedConnections)
        {
            ConnectionManager.RemoveConnectionDeferred(droppedConn);
            var droppedVm = Connections.FirstOrDefault(c => c.Connection == droppedConn);
            if (droppedVm != null) Connections.Remove(droppedVm);
        }

        RefreshPinViewModels(component);

        if (ConnectionManager.Connections.Count > 0) _ = RecalculateRoutesAsync();
        InvalidateSimulation();
        return result.Warnings;
    }

    /// <summary>
    /// Drops the pin view-models that still reference the component's replaced pins
    /// and re-creates them from the component's current <see cref="Component.PhysicalPins"/>.
    /// </summary>
    private void RefreshPinViewModels(Component component)
    {
        var staleVms = AllPins.Where(p => p.Pin.ParentComponent == component).ToList();
        foreach (var pinVm in staleVms) AllPins.Remove(pinVm);

        var compVm = Components.FirstOrDefault(c => c.Component == component);
        if (compVm == null) return;
        foreach (var pin in component.PhysicalPins)
            AllPins.Add(new PinViewModel(pin, compVm));
    }
}
