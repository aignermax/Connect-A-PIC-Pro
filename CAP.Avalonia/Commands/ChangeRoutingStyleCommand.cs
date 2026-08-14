using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Undoable routing-style change for one or many connections (issue #862). Applying a style to
/// a multi-selection is ONE command, so a single Ctrl+Z restores every connection's previous
/// style. Metal traces route with curved bends like waveguides (#854), so electrical
/// connections are restyled the same way as optical ones.
/// </summary>
public sealed class ChangeRoutingStyleCommand : IUndoableCommand
{
    private sealed record ConnectionState(
        WaveguideConnectionViewModel Connection,
        WaveguideType BeforeType,
        bool BeforeFrozen,
        RoutedPath? BeforePath,
        Dictionary<int, double> BeforeBendOverrides,
        Dictionary<int, double> BeforeStraightShifts);

    private readonly DesignCanvasViewModel _canvas;
    private readonly List<ConnectionState> _states;
    private readonly WaveguideType _style;

    /// <summary>Initializes a new instance of <see cref="ChangeRoutingStyleCommand"/>.</summary>
    /// <param name="canvas">The design canvas whose routes are recalculated after each apply.</param>
    /// <param name="connections">The connections to restyle.</param>
    /// <param name="style">The routing style to apply to all of them.</param>
    public ChangeRoutingStyleCommand(
        DesignCanvasViewModel canvas,
        IEnumerable<WaveguideConnectionViewModel> connections,
        WaveguideType style)
    {
        _canvas = canvas;
        _style = style;
        _states = connections
            .Distinct()
            .Select(c => new ConnectionState(
                c,
                c.Connection.Type,
                c.Connection.IsRouteFrozen,
                c.Connection.RoutedPath,
                new Dictionary<int, double>(c.Connection.BendRadiusOverrides),
                new Dictionary<int, double>(c.Connection.StraightShiftOffsets)))
            .ToList();
    }

    /// <summary>Number of connections this command actually restyles.</summary>
    public int AffectedCount => _states.Count;

    /// <inheritdoc/>
    public string Description => _states.Count == 1
        ? $"Set routing style to {_style}"
        : $"Set routing style to {_style} ({_states.Count} connections)";

    /// <inheritdoc/>
    public void Execute()
    {
        foreach (var state in _states)
            ApplyStyle(state.Connection.Connection, _style);
        RecalculateOnce();
    }

    /// <inheritdoc/>
    public void Undo()
    {
        foreach (var state in _states)
            RestoreState(state);
        RecalculateOnce();
    }

    /// <summary>
    /// Restores the full pre-command state: frozen (e.g. GDS-imported) route geometry and manual
    /// bend edits would be discarded for good by <see cref="ApplyStyle"/>'s route invalidation,
    /// so the captured path is put back and the frozen flag re-set before routes recalculate.
    /// </summary>
    private static void RestoreState(ConnectionState state)
    {
        var connection = state.Connection.Connection;
        connection.Type = state.BeforeType;
        connection.IsRouteFrozen = state.BeforeFrozen;
        RestoreEntries(connection.BendRadiusOverrides, state.BeforeBendOverrides);
        RestoreEntries(connection.StraightShiftOffsets, state.BeforeStraightShifts);
        if (state.BeforePath != null && (state.BeforeFrozen || connection.HasManualPathEdits))
            connection.RestoreCachedPath(state.BeforePath);
        else
            connection.InvalidateRoute();
    }

    private static void RestoreEntries(Dictionary<int, double> target, Dictionary<int, double> saved)
    {
        target.Clear();
        foreach (var (segmentIndex, value) in saved)
            target[segmentIndex] = value;
    }

    /// <summary>
    /// Mirrors the single-connection style-change semantics: Auto releases the frozen route so
    /// the A* router takes over; any style change invalidates the current route because
    /// incremental routing would otherwise keep the stale geometry until a component moves.
    /// Width/radius stay the connection's own values (see ConnectionRoutingViewModel).
    /// </summary>
    private static void ApplyStyle(WaveguideConnection connection, WaveguideType style)
    {
        connection.Type = style;
        if (style == WaveguideType.Auto)
            connection.IsRouteFrozen = false;
        connection.InvalidateRoute();
    }

    private void RecalculateOnce()
    {
        if (_states.Count > 0)
            _ = _canvas.RecalculateRoutesAsync();
    }
}
