using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.CrossingInsertion;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Inserts a user-placed crossing component at a Cut-tool candidate point (issue #798):
/// centers the PDK crossing on the intersection, removes the original connection and
/// docks its two halves onto the crossing's through ports. Unlike adaptive crossings
/// (issue #553) the component is deliberate user intent: <c>IsInsertedCrossing</c>
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

    private ComponentViewModel? _componentViewModel;
    private List<WaveguideConnection>? _subConnections;
    private bool _failed;

    /// <summary>Creates the command for one candidate and a fresh crossing instance.</summary>
    public InsertManualCrossingCommand(
        DesignCanvasViewModel canvas,
        ManualCrossingCandidate candidate,
        CrossingComponentInstance crossingInstance)
    {
        _canvas = canvas;
        _candidate = candidate;
        _crossingInstance = crossingInstance;
        _original = candidate.Connection;
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
            RestoreConnection(sub);

        _canvas.InvalidateSimulation();
        _ = _canvas.RecalculateRoutesAsync();
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_failed || _subConnections == null || _componentViewModel == null) return;

        foreach (var sub in _subConnections)
            RemoveConnection(sub);
        _canvas.RemoveComponent(_componentViewModel);

        RestoreConnection(_original);
        _canvas.InvalidateSimulation();
        _ = _canvas.RecalculateRoutesAsync();
    }

    /// <summary>
    /// First-run setup: resolves the crossing's four cardinal ports and builds the
    /// two half-connections. Marks the command failed (a safe no-op) when the
    /// component lacks a required port — nothing was mutated yet at that point.
    /// </summary>
    private bool TryPrepare()
    {
        var crossing = _crossingInstance.Component;
        CenterCrossingOnIntersection(crossing);

        var (entry, exit) = ResolveThroughPorts(crossing);
        if (entry?.LogicalPin == null || exit?.LogicalPin == null)
        {
            _failed = true;
            return false;
        }

        _subConnections = new List<WaveguideConnection>
        {
            CreateSubConnection(_original.StartPin, entry),
            CreateSubConnection(exit, _original.EndPin),
        };
        return true;
    }

    /// <summary>
    /// Picks the crossing ports the split connection docks onto: the two ports on
    /// the segment axis, oriented by travel direction (+X enters west, +Y enters north).
    /// </summary>
    private (PhysicalPin? Entry, PhysicalPin? Exit) ResolveThroughPorts(Component crossing)
    {
        var west = CrossingPlacement.FindPinByAngle(crossing, 180);
        var east = CrossingPlacement.FindPinByAngle(crossing, 0);
        var north = CrossingPlacement.FindPinByAngle(crossing, 270);
        var south = CrossingPlacement.FindPinByAngle(crossing, 90);

        if (_candidate.SegmentIsHorizontal)
            return _candidate.SegmentDirection.X > 0 ? (west, east) : (east, west);
        return _candidate.SegmentDirection.Y > 0 ? (north, south) : (south, north);
    }

    private void CenterCrossingOnIntersection(Component crossing)
    {
        crossing.PhysicalX = _candidate.IntersectionPoint.X - crossing.WidthMicrometers / 2.0;
        crossing.PhysicalY = _candidate.IntersectionPoint.Y - crossing.HeightMicrometers / 2.0;
    }

    private WaveguideConnection CreateSubConnection(PhysicalPin startPin, PhysicalPin endPin)
    {
        return new WaveguideConnection
        {
            StartPin = startPin,
            EndPin = endPin,
            WidthMicrometers = _original.WidthMicrometers,
            BendRadiusMicrometers = _original.BendRadiusMicrometers,
            PropagationLossDbPerCm = _original.PropagationLossDbPerCm,
            BendLossDbPer90Deg = _original.BendLossDbPer90Deg,
            DispersionModel = _original.DispersionModel,
        };
    }

    private void RemoveConnection(WaveguideConnection connection)
    {
        _canvas.ConnectionManager.RemoveConnectionDeferred(connection);
        var vm = _canvas.Connections.FirstOrDefault(c => c.Connection == connection);
        if (vm != null)
            _canvas.Connections.Remove(vm);
    }

    private void RestoreConnection(WaveguideConnection connection)
    {
        _canvas.ConnectionManager.AddExistingConnection(connection);
        if (!_canvas.Connections.Any(c => c.Connection == connection))
            _canvas.Connections.Add(new WaveguideConnectionViewModel(connection));
    }
}
