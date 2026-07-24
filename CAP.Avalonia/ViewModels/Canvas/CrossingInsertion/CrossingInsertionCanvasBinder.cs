using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.CrossingInsertion;

namespace CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;

/// <summary>
/// Wires the adaptive crossing-insertion feature (Issue #553) into the design
/// canvas: constructs the <see cref="CrossingInsertionService"/> with a factory
/// producing real PDK crossing components, assigns it to the canvas'
/// <see cref="WaveguideConnectionManager"/>, and keeps the canvas view-model
/// collections (components, pins, connections) in sync when crossings are
/// inserted or dissolved — so rendering, export and hierarchy see the crossing
/// like a normally placed component.
/// </summary>
public class CrossingInsertionCanvasBinder
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly Func<CrossingComponentInstance?> _crossingFactory;
    private readonly Action<Action> _dispatch;

    private string? _templateName;
    private string? _templatePdkSource;

    /// <summary>The crossing-insertion service wired to the canvas' connection manager.</summary>
    public CrossingInsertionService Service { get; }

    /// <summary>
    /// Creates the binder and attaches crossing insertion to the given canvas.
    /// </summary>
    /// <param name="canvas">The design canvas whose connection manager gets the crossing pass.</param>
    /// <param name="crossingFactory">
    /// Factory producing a fresh crossing component from the loaded PDK library.
    /// May return null while no crossing PDK component is available — the pass
    /// is then skipped and detours are kept.
    /// </param>
    /// <param name="uiDispatch">
    /// Marshals canvas collection updates to the UI thread. Defaults to the
    /// Avalonia UI dispatcher; tests can pass an inline dispatcher.
    /// </param>
    public CrossingInsertionCanvasBinder(
        DesignCanvasViewModel canvas,
        Func<CrossingComponentInstance?> crossingFactory,
        Action<Action>? uiDispatch = null)
    {
        _canvas = canvas;
        _crossingFactory = crossingFactory;
        _dispatch = uiDispatch ?? DispatchToUiThread;

        Service = new CrossingInsertionService(CreateCrossingComponent)
        {
            ComponentAdded = OnCrossingAdded,
            ComponentRemoved = OnCrossingRemoved,
        };
        // New features default to OFF: the service is not attached until the
        // user enables crossing insertion (Settings → Routing → Crossings).
    }

    /// <summary>
    /// Whether adaptive crossing insertion runs after routing (Issue #553). Off by
    /// default. Toggling on attaches the service to the connection manager; toggling
    /// off detaches it, so routing immediately reverts to classic avoid-only behavior
    /// (no extra post-routing pass). Already-inserted crossings persist until the next
    /// recalculation. Bound to the Routing settings page.
    /// </summary>
    public bool IsEnabled
    {
        get => _canvas.ConnectionManager.CrossingInsertion != null;
        set
        {
            _canvas.ConnectionManager.CrossingInsertion = value ? Service : null;
            if (value)
                RebuildRecordsForExistingCrossings();
        }
    }

    /// <summary>
    /// Rebuilds dissolution records for auto-inserted crossings that exist on the
    /// canvas without a record (#705) — e.g. loaded from file, or placed before the
    /// feature was toggled off and on again. Idempotent: recorded crossings are skipped.
    /// </summary>
    private void RebuildRecordsForExistingCrossings()
    {
        CrossingRecordRebuilder.Rebuild(
            Service,
            _canvas.ConnectionManager,
            _canvas.Components.Select(vm => vm.Component));
    }

    /// <summary>Creates a fresh crossing component and remembers its template metadata.</summary>
    private Component? CreateCrossingComponent()
    {
        var instance = _crossingFactory();
        if (instance == null) return null;

        _templateName = instance.TemplateName;
        _templatePdkSource = instance.TemplatePdkSource;
        return instance.Component;
    }

    /// <summary>
    /// Called by the service (on the routing thread) after a crossing was placed.
    /// Snapshots the connection list on the calling thread, then updates the
    /// canvas view-model collections on the UI thread.
    /// </summary>
    private void OnCrossingAdded(Component crossing)
    {
        var connections = _canvas.ConnectionManager.Connections.ToList();
        _dispatch(() =>
        {
            AddCrossingViewModel(crossing);
            SyncConnectionViewModels(connections);
        });
    }

    /// <summary>Called by the service after a crossing was dissolved.</summary>
    private void OnCrossingRemoved(Component crossing)
    {
        var connections = _canvas.ConnectionManager.Connections.ToList();
        _dispatch(() =>
        {
            RemoveCrossingViewModel(crossing);
            SyncConnectionViewModels(connections);
        });
    }

    /// <summary>
    /// Adds the crossing's component and pin view-models. The pathfinding-grid
    /// obstacle was already registered by the service on the routing thread, so
    /// this only touches the UI collections (no <see cref="DesignCanvasViewModel.AddComponent"/>,
    /// which would re-register the obstacle concurrently with routing).
    /// </summary>
    private void AddCrossingViewModel(Component crossing)
    {
        if (_canvas.Components.Any(vm => vm.Component == crossing)) return;

        var vm = new ComponentViewModel(crossing, _templateName, _templatePdkSource);
        _canvas.Components.Add(vm);
        foreach (var pin in crossing.PhysicalPins)
            _canvas.AllPins.Add(new PinViewModel(pin, vm));
    }

    /// <summary>Removes the crossing's component and pin view-models.</summary>
    private void RemoveCrossingViewModel(Component crossing)
    {
        var vm = _canvas.Components.FirstOrDefault(c => c.Component == crossing);
        if (vm == null) return;

        var stalePins = _canvas.AllPins
            .Where(p => p.ParentComponentViewModel == vm)
            .ToList();
        foreach (var pin in stalePins)
            _canvas.AllPins.Remove(pin);

        _canvas.Components.Remove(vm);
    }

    /// <summary>
    /// Reconciles the canvas connection view-models with the connection manager:
    /// split originals disappear, crossing sub-connections (and restored
    /// survivors after dissolution) appear.
    /// </summary>
    private void SyncConnectionViewModels(List<WaveguideConnection> managedConnections)
    {
        var stale = _canvas.Connections
            .Where(vm => !managedConnections.Contains(vm.Connection))
            .ToList();
        foreach (var vm in stale)
            _canvas.Connections.Remove(vm);

        var wrapped = _canvas.Connections.Select(vm => vm.Connection).ToHashSet();
        foreach (var connection in managedConnections.Where(c => !wrapped.Contains(c)))
            _canvas.Connections.Add(new WaveguideConnectionViewModel(connection));
    }

    /// <summary>Runs the action on the Avalonia UI thread (inline when already on it).</summary>
    private static void DispatchToUiThread(Action action)
    {
        var dispatcher = global::Avalonia.Threading.Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Post(action);
    }
}
