using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Undoable routing-style change for one or many waveguide connections (issue #862). Applying a
/// style to a multi-selection is ONE command, so a single Ctrl+Z restores every connection's
/// previous style. Electrical connections are filtered out at construction: metal traces carry no
/// routing style (issue #682; to be revisited with curved metal, issue #854).
/// </summary>
public sealed class ChangeRoutingStyleCommand : IUndoableCommand
{
    private sealed record ConnectionState(WaveguideConnectionViewModel Connection, WaveguideType BeforeType);

    private readonly DesignCanvasViewModel _canvas;
    private readonly List<ConnectionState> _states;
    private readonly WaveguideType _style;

    /// <summary>Initializes a new instance of <see cref="ChangeRoutingStyleCommand"/>.</summary>
    /// <param name="canvas">The design canvas whose routes are recalculated after each apply.</param>
    /// <param name="connections">The connections to restyle; electrical ones are skipped.</param>
    /// <param name="style">The routing style to apply to all of them.</param>
    public ChangeRoutingStyleCommand(
        DesignCanvasViewModel canvas,
        IEnumerable<WaveguideConnectionViewModel> connections,
        WaveguideType style)
    {
        _canvas = canvas;
        _style = style;
        _states = connections
            .Where(c => !c.Connection.IsElectrical)
            .Distinct()
            .Select(c => new ConnectionState(c, c.Connection.Type))
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
            ApplyStyle(state.Connection.Connection, state.BeforeType);
        RecalculateOnce();
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
