using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for deleting multiple components as a single undoable operation.
/// Stores all affected components and their connections for undo.
/// </summary>
public class GroupDeleteCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly List<DeletedComponentData> _deletedComponents = new();

    /// <summary>
    /// Components actually removed by the last <see cref="Execute"/> run.
    /// Locked components are skipped on Execute and must not be re-added on Undo,
    /// otherwise they would end up on the canvas twice.
    /// </summary>
    private readonly List<DeletedComponentData> _removedComponents = new();

    /// <summary>
    /// Creates a group delete command for the given components.
    /// </summary>
    public GroupDeleteCommand(
        DesignCanvasViewModel canvas,
        IReadOnlyList<ComponentViewModel> components)
    {
        _canvas = canvas;

        // Snapshot each component and its connections before deletion
        foreach (var comp in components)
        {
            var connections = canvas.Connections
                .Where(c => c.Connection.StartPin.ParentComponent == comp.Component ||
                            c.Connection.EndPin.ParentComponent == comp.Component)
                .Select(c => (c.Connection, c))
                .ToList();

            _deletedComponents.Add(new DeletedComponentData(
                comp,
                comp.Component,
                comp.TemplateName,
                comp.TemplatePdkSource,
                comp.X,
                comp.Y,
                connections));
        }
    }

    /// <inheritdoc />
    public string Description => $"Delete {_deletedComponents.Count} components";

    /// <inheritdoc />
    public void Execute()
    {
        _removedComponents.Clear();

        // Remove all components (RemoveComponent also removes their connections)
        foreach (var data in _deletedComponents)
        {
            // Skip locked components
            if (data.Component.IsLocked)
                continue;

            // Snapshot connections that still exist (may have been removed by prior deletion)
            data.Connections.Clear();
            foreach (var connVm in _canvas.Connections.ToList())
            {
                if (connVm.Connection.StartPin.ParentComponent == data.Component ||
                    connVm.Connection.EndPin.ParentComponent == data.Component)
                {
                    data.Connections.Add((connVm.Connection, connVm));
                }
            }

            _canvas.RemoveComponent(data.ViewModel);
            _removedComponents.Add(data);
        }
    }

    /// <inheritdoc />
    public void Undo()
    {
        // Re-add only the components Execute actually removed (locked ones stayed put)
        foreach (var data in _removedComponents)
        {
            data.Component.PhysicalX = data.X;
            data.Component.PhysicalY = data.Y;
            data.ViewModel = _canvas.AddComponent(data.Component, data.TemplateName, data.TemplatePdkSource);
        }

        // Re-add connections (deduplicate by connection identity)
        var restoredConnections = new HashSet<WaveguideConnection>();
        foreach (var data in _removedComponents)
        {
            foreach (var (connection, _) in data.Connections)
            {
                if (restoredConnections.Add(connection))
                {
                    _canvas.ConnectionManager.AddExistingConnection(connection);
                    var connVm = new WaveguideConnectionViewModel(connection);
                    _canvas.Connections.Add(connVm);
                }
            }
        }

        _ = _canvas.RecalculateRoutesAsync();
        _canvas.InvalidateSimulation();
    }

    /// <summary>
    /// Mutable data for a single deleted component.
    /// </summary>
    private sealed class DeletedComponentData
    {
        public ComponentViewModel ViewModel { get; set; }
        public Component Component { get; }
        public string? TemplateName { get; }
        public string? TemplatePdkSource { get; }
        public double X { get; }
        public double Y { get; }
        public List<(WaveguideConnection Connection, WaveguideConnectionViewModel Vm)> Connections { get; }

        public DeletedComponentData(
            ComponentViewModel viewModel,
            Component component,
            string? templateName,
            string? templatePdkSource,
            double x,
            double y,
            List<(WaveguideConnection, WaveguideConnectionViewModel)> connections)
        {
            ViewModel = viewModel;
            Component = component;
            TemplateName = templateName;
            TemplatePdkSource = templatePdkSource;
            X = x;
            Y = y;
            Connections = connections;
        }
    }
}
