using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Single undoable step that records the before/after state of a "Re-route imported
/// routes" pass. The initial routing is performed asynchronously by the ViewModel;
/// this command is pushed afterwards so one Ctrl+Z restores both canvas connections
/// and group-internal frozen paths to their exact pre-reroute geometry.
/// </summary>
public sealed class RerouteImportedRoutesStateCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly IReadOnlyList<ConnectionState> _connections;
    private readonly IReadOnlyList<GroupState> _groups;

    /// <summary>Mutable state of one canvas-level connection.</summary>
    public sealed class ConnectionState
    {
        /// <summary>The connection whose geometry is swapped.</summary>
        public required WaveguideConnection Connection { get; set; }

        /// <summary>Frozen geometry before the re-route.</summary>
        public required RoutedPath OldPath { get; set; }

        /// <summary>Freeze flag before the re-route.</summary>
        public required bool OldIsFrozen { get; set; }

        /// <summary>Live geometry after the re-route.</summary>
        public RoutedPath NewPath { get; set; } = null!;

        /// <summary>Freeze flag after the re-route.</summary>
        public bool NewIsFrozen { get; set; }
    }

    /// <summary>Mutable state of one component group's internal frozen paths.</summary>
    public sealed class GroupState
    {
        /// <summary>The group whose internal paths are swapped.</summary>
        public required ComponentGroup Group { get; set; }

        /// <summary>All internal paths before the re-route.</summary>
        public required IReadOnlyList<FrozenWaveguidePath> OldPaths { get; set; }

        /// <summary>All internal paths after the re-route.</summary>
        public IReadOnlyList<FrozenWaveguidePath> NewPaths { get; set; } = null!;
    }

    /// <summary>Records the state change; the caller already performed the mutation.</summary>
    public RerouteImportedRoutesStateCommand(
        DesignCanvasViewModel canvas,
        IEnumerable<ConnectionState> connections,
        IEnumerable<GroupState> groups)
    {
        _canvas = canvas;
        _connections = connections.ToList();
        _groups = groups.ToList();
    }

    /// <inheritdoc/>
    public string Description
    {
        get
        {
            var count = _connections.Count + _groups.Sum(g => g.NewPaths.Count);
            return count == 1
                ? "Re-route imported route"
                : $"Re-route {count} imported routes";
        }
    }

    /// <inheritdoc/>
    public void Execute() => Apply(useNew: true);

    /// <inheritdoc/>
    public void Undo() => Apply(useNew: false);

    private void Apply(bool useNew)
    {
        foreach (var state in _connections)
        {
            var path = useNew ? state.NewPath : state.OldPath;
            var frozen = useNew ? state.NewIsFrozen : state.OldIsFrozen;
            state.Connection.RestoreCachedPath(path.DeepCopy());
            state.Connection.IsRouteFrozen = frozen;
        }

        foreach (var state in _groups)
        {
            var paths = useNew ? state.NewPaths : state.OldPaths;
            state.Group.InternalPaths.Clear();
            state.Group.AddInternalPaths(paths.Select(CloneWithPins).ToList());
        }

        _canvas.InvalidateSimulation();
        _ = _canvas.RecalculateRoutesAsync();
    }

    /// <summary>
    /// Deep-clones a frozen path while preserving its pin references, so a restored
    /// group path still anchors to the correct child-component pins.
    /// </summary>
    public static FrozenWaveguidePath CloneWithPins(FrozenWaveguidePath path)
    {
        var clone = new FrozenWaveguidePath
        {
            Path = path.Path.DeepCopy(),
            PathId = path.PathId,
            StartPin = path.StartPin,
            EndPin = path.EndPin
        };
        clone.CopySettingsFrom(path);
        return clone;
    }
}
