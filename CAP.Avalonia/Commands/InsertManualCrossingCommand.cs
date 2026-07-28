using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP_Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.CrossingInsertion;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Inserts a user-placed crossing component at a Cut-tool candidate point:
/// centers the PDK crossing on the intersection, removes the original connection and
/// docks its two halves onto the crossing's through ports. Unlike adaptive crossings,
/// the component is deliberate user intent: <c>IsInsertedCrossing</c>
/// stays false, so the adaptive pass never dissolves or moves it. Undo removes the
/// crossing and restores the original connection object — its fine-tuning
/// (bend-radius overrides, segment shifts) survives untouched.
/// </summary>
public class InsertManualCrossingCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly ManualCrossingCandidate _candidate;
    private readonly CrossingComponentInstance _crossingInstance;
    private readonly WaveguideConnection _original;
    private readonly ErrorConsoleService? _errorConsole;

    private ComponentViewModel? _componentViewModel;
    private List<WaveguideConnection>? _subConnections;
    private bool _failed;

    /// <summary>Creates the command for one candidate and a fresh crossing instance.</summary>
    /// <param name="errorConsole">
    /// Optional sink for the post-mutation routing pass's exceptions, which run fire-and-forget
    /// (see <see cref="Execute"/>) and must never crash unobserved on a background thread.
    /// </param>
    public InsertManualCrossingCommand(
        DesignCanvasViewModel canvas,
        ManualCrossingCandidate candidate,
        CrossingComponentInstance crossingInstance,
        ErrorConsoleService? errorConsole = null)
    {
        _canvas = canvas;
        _candidate = candidate;
        _crossingInstance = crossingInstance;
        _original = candidate.Connection;
        _errorConsole = errorConsole;
    }

    /// <inheritdoc/>
    public string Description => $"Insert crossing into {_original.StartPin.Name}–{_original.EndPin.Name}";

    /// <inheritdoc/>
    public void Execute()
    {
        if (_failed) return;
        if (_subConnections == null && !TryPrepare()) return;

        var crossing = _crossingInstance.Component;
        CenterCrossingOnIntersection(crossing);
        _componentViewModel = _canvas.AddComponent(
            crossing, _crossingInstance.TemplateName, _crossingInstance.TemplatePdkSource);

        RemoveConnection(_original);
        foreach (var sub in _subConnections!)
            _canvas.RestoreConnectionAndViewModel(sub);

        _canvas.InvalidateSimulation();
        FireAndForgetReroute();
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_failed || _subConnections == null || _componentViewModel == null) return;

        foreach (var sub in _subConnections)
            RemoveConnection(sub);
        _canvas.RemoveComponent(_componentViewModel);

        _canvas.RestoreConnectionAndViewModel(_original);
        _canvas.InvalidateSimulation();
        FireAndForgetReroute();
    }

    /// <summary>
    /// First-run setup: resolves the crossing's four cardinal ports and builds the
    /// two half-connections. Marks the command failed (a safe no-op) when the component
    /// lacks a required port, or when the original connection is no longer registered with
    /// the canvas — a stale candidate re-clicked before the next frame refreshed the
    /// candidate list must not stack a second crossing onto an already-split connection.
    /// Nothing was mutated yet at either failure point.
    /// </summary>
    private bool TryPrepare()
    {
        if (!_canvas.ConnectionManager.Connections.Contains(_original))
        {
            _failed = true;
            return false;
        }

        var crossing = _crossingInstance.Component;
        CenterCrossingOnIntersection(crossing);

        var (entry, exit) = CrossingPlacement.ResolveThroughPorts(
            crossing, _candidate.SegmentIsHorizontal, _candidate.SegmentDirection);
        if (entry?.LogicalPin == null || exit?.LogicalPin == null)
        {
            _failed = true;
            return false;
        }

        _subConnections = new List<WaveguideConnection>
        {
            CrossingPlacement.CreateSubConnection(_original, _original.StartPin, entry),
            CrossingPlacement.CreateSubConnection(_original, exit, _original.EndPin),
        };
        return true;
    }

    private void CenterCrossingOnIntersection(Component crossing)
    {
        crossing.PhysicalX = _candidate.IntersectionPoint.X - crossing.WidthMicrometers / 2.0;
        crossing.PhysicalY = _candidate.IntersectionPoint.Y - crossing.HeightMicrometers / 2.0;
    }

    private void RemoveConnection(WaveguideConnection connection)
    {
        _canvas.ConnectionManager.RemoveConnectionDeferred(connection);
        var vm = _canvas.Connections.FirstOrDefault(c => c.Connection == connection);
        if (vm != null)
            _canvas.Connections.Remove(vm);
    }

    /// <summary>
    /// Starts the post-mutation reroute without blocking Execute/Undo on it — awaiting here
    /// would risk a UI-thread deadlock against the pass's own dispatcher-posted continuations
    /// (see <c>RoutingOrchestrator</c>). Observes the task instead of discarding it outright,
    /// so a failure surfaces through the error console rather than as an unobserved exception.
    /// </summary>
    private void FireAndForgetReroute()
    {
        _ = ObserveRerouteAsync();
    }

    private async Task ObserveRerouteAsync()
    {
        try
        {
            await _canvas.RecalculateRoutesAsync();
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError("Cut tool: routing after crossing insert/undo failed", ex);
        }
    }
}
