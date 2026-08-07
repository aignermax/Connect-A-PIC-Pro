using System.Text.Json;
using System.Text.Json.Serialization;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Selection;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Components.Process;

namespace CAP.Avalonia.Services;

/// <summary>
/// Implements AI-accessible grid operations by wrapping existing canvas and simulation services.
/// Translates high-level AI commands into concrete application actions.
/// </summary>
public class AiGridService : IAiGridService
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly LeftPanelViewModel _leftPanel;
    private readonly SimulationService _simulationService;

    /// <summary>
    /// Shared placement-policy context (issues #570/#653/#737). Wired by <c>MainViewModel</c>
    /// to the same instance <c>CanvasInteractionViewModel</c> uses, so the AI placement path
    /// obeys exactly the same single-process enforcement as manual placement.
    /// Defaults to <see cref="PlacementPolicyContext.Unrestricted"/>.
    /// </summary>
    public PlacementPolicyContext PlacementContext { get; set; } = PlacementPolicyContext.Unrestricted;

    /// <summary>
    /// Snapshot of the process-guard inputs (active process, agnostic tool PDKs, live by-value
    /// member set), resolved ONCE per public entry point and reused across per-template /
    /// per-clipboard-item loops — re-invoking the callbacks per candidate made
    /// <see cref="GetAvailableComponentTypes"/> O(Templates × PDKs) (review Finding 5).
    /// Short-lived (one call); never cached across calls, so it can't go stale.
    /// </summary>
    private sealed record ProcessGuard(
        ActiveProcessSelection? Active,
        IReadOnlyCollection<string> AgnosticPdkNames,
        IReadOnlyCollection<string>? LiveMemberPdkNames);

    /// <summary>Resolves the guard inputs from the shared context (see <see cref="ProcessGuard"/>).</summary>
    private ProcessGuard ResolveProcessGuard() => new(
        PlacementContext.ActiveProcess,
        PlacementContext.ProcessAgnosticPdkNames,
        PlacementContext.LiveMemberPdkNames);

    private static (bool IsAllowed, string? BlockReason) CheckProcess(string? pdkSource, ProcessGuard guard) =>
        SingleProcessPolicy.CheckPlacement(guard.Active, pdkSource, guard.AgnosticPdkNames, guard.LiveMemberPdkNames);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes the grid service with required canvas and simulation dependencies.
    /// </summary>
    public AiGridService(
        DesignCanvasViewModel canvas,
        LeftPanelViewModel leftPanel,
        SimulationService simulationService)
    {
        _canvas = canvas;
        _leftPanel = leftPanel;
        _simulationService = simulationService;
    }

    /// <inheritdoc/>
    public string GetGridState()
    {
        var connectedPins = GetConnectedPins();
        var state = new
        {
            components = _canvas.Components.Select(c =>
            {
                var group = c.Component as ComponentGroup;
                return new
                {
                    id = c.Component.Identifier,
                    type = c.TemplateName ?? c.Component.HumanReadableName ?? c.Component.Identifier,
                    position = new
                    {
                        x = Math.Round(c.Component.PhysicalX, 1),
                        y = Math.Round(c.Component.PhysicalY, 1)
                    },
                    rotation = c.Component.RotationDegrees,
                    pins = group != null ? group.ExternalPins.Count : c.Component.PhysicalPins.Count,
                    external_pins = group != null && group.ExternalPins.Count > 0
                        ? (object)group.ExternalPins.Select(pin => new
                        {
                            name = pin.Name,
                            direction = pin.AngleDegrees,
                            connected = connectedPins.Contains(pin.InternalPin)
                        }).ToList()
                        : null
                };
            }).ToList(),
            connections = _canvas.Connections.Select(conn => new
            {
                from_component = conn.Connection.StartPin.ParentComponent?.Identifier,
                from_pin = conn.Connection.StartPin.Name,
                to_component = conn.Connection.EndPin.ParentComponent?.Identifier,
                to_pin = conn.Connection.EndPin.Name
            }).ToList(),
            available_types = GetAvailableComponentTypes().Take(20).ToList(),
            grid_size_um = new { width = 5000, height = 5000 }
        };

        return JsonSerializer.Serialize(state, JsonOptions);
    }

    /// <inheritdoc/>
    public async Task<string> PlaceComponentAsync(string componentType, double x, double y, int rotation = 0)
    {
        var template = _leftPanel.AllTemplates
            .FirstOrDefault(t => t.Name.Equals(componentType, StringComparison.OrdinalIgnoreCase));

        if (template == null)
        {
            var available = GetAvailableComponentTypes().Take(15).ToList();
            return $"Component type '{componentType}' not found. Available types include: {string.Join(", ", available)}";
        }

        // Single-process enforcement (issue #570): the AI path must not bypass the
        // same gate manual placement goes through.
        var (isAllowed, blockReason) = CheckProcess(template.PdkSource, ResolveProcessGuard());
        if (!isAllowed)
            return blockReason ?? $"Cannot place '{componentType}' — it belongs to another process.";

        // Center the component on the requested position
        var centeredX = x - template.WidthMicrometers / 2;
        var centeredY = y - template.HeightMicrometers / 2;

        var cmd = PlaceComponentCommand.TryCreate(_canvas, template, centeredX, centeredY);
        if (cmd == null)
            return $"Cannot place '{componentType}' — no valid position found near ({x:F0}, {y:F0})µm. Try a different position.";

        cmd.Execute();

        var placed = _canvas.Components.LastOrDefault();
        if (placed == null)
            return $"Failed to place '{componentType}'.";

        var px = Math.Round(placed.Component.PhysicalX, 0);
        var py = Math.Round(placed.Component.PhysicalY, 0);
        return $"Placed '{placed.Component.Identifier}' at ({px}, {py})µm";
    }

    /// <inheritdoc/>
    public async Task<string> CreateConnectionAsync(string fromComponentId, string toComponentId)
    {
        var fromVm = _canvas.Components.FirstOrDefault(c =>
            c.Component.Identifier.Equals(fromComponentId, StringComparison.OrdinalIgnoreCase));
        var toVm = _canvas.Components.FirstOrDefault(c =>
            c.Component.Identifier.Equals(toComponentId, StringComparison.OrdinalIgnoreCase));

        if (fromVm == null) return $"Component '{fromComponentId}' not found";
        if (toVm == null) return $"Component '{toComponentId}' not found";

        var alreadyConnected = GetConnectedPins();

        var fromPins = GetEffectivePins(fromVm.Component);
        var toPins = GetEffectivePins(toVm.Component);

        var fromPin = FindBestPin(fromPins, alreadyConnected, preferOutput: true);
        var toPin = FindBestPin(toPins, alreadyConnected, preferOutput: false);

        if (fromPin == null) return $"No available output pins on '{fromComponentId}'";
        if (toPin == null) return $"No available input pins on '{toComponentId}'";

        await _canvas.ConnectPinsAsync(fromPin, toPin);
        return $"Connected '{fromComponentId}'.{fromPin.Name} → '{toComponentId}'.{toPin.Name}";
    }

    /// <inheritdoc/>
    public async Task<string> RunSimulationAsync()
    {
        if (_canvas.Components.Count == 0)
            return "No components on canvas to simulate.";

        if (_canvas.Connections.Count == 0)
            return "No connections found. Connect components before running simulation.";

        var result = await _simulationService.RunAsync(_canvas);
        if (!result.Success)
            return $"Simulation failed: {result.ErrorMessage ?? "unknown error"}";

        return "Simulation completed. Use get_light_values to read results.";
    }

    /// <inheritdoc/>
    public string GetLightValues()
    {
        var connections = _canvas.Connections.Select(c => new
        {
            from = c.Connection.StartPin.ParentComponent?.Identifier,
            to = c.Connection.EndPin.ParentComponent?.Identifier,
            loss_db = Math.Round(c.LossDb, 2),
            length_um = Math.Round(c.PathLength, 1)
        }).ToList();

        return JsonSerializer.Serialize(new { connections }, JsonOptions);
    }

    /// <inheritdoc/>
    public string ClearGrid()
    {
        var components = _canvas.Components.ToList();
        foreach (var comp in components)
            _canvas.RemoveComponent(comp);

        return $"Grid cleared. Removed {components.Count} component(s).";
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetAvailableComponentTypes()
    {
        var guard = ResolveProcessGuard();
        return _leftPanel.AllTemplates
            .Where(t => CheckProcess(t.PdkSource, guard).IsAllowed)
            .Select(t => t.Name).Distinct().ToList();
    }

    /// <inheritdoc/>
    public string CreateGroup(IReadOnlyList<string> componentIds, string? groupName = null)
    {
        var componentsToGroup = componentIds
            .Select(id => _canvas.Components.FirstOrDefault(c => c.Component.Identifier == id))
            .Where(c => c != null)
            .Cast<ComponentViewModel>()
            .ToList();

        if (componentsToGroup.Count < 2)
            return $"Cannot create group: need at least 2 components. Found {componentsToGroup.Count} matching IDs.";

        if (componentsToGroup.Any(c => c.Component.IsLocked))
            return "Cannot group locked components.";

        var cmd = new CreateGroupCommand(_canvas, componentsToGroup);
        cmd.Execute();

        var createdGroup = _canvas.Components.LastOrDefault();
        if (createdGroup?.Component is ComponentGroup group)
        {
            if (!string.IsNullOrWhiteSpace(groupName))
                group.GroupName = groupName;

            return $"Created group '{group.Identifier}' (name: {group.GroupName}) with {componentsToGroup.Count} components.";
        }

        return "Failed to create group.";
    }

    /// <inheritdoc/>
    public string UngroupComponent(string groupId)
    {
        var groupVm = _canvas.Components.FirstOrDefault(c => c.Component.Identifier == groupId);
        if (groupVm?.Component is not ComponentGroup group)
            return $"Component '{groupId}' is not a group.";

        var memberCount = group.ChildComponents.Count;
        var cmd = new UngroupCommand(_canvas, group);
        cmd.Execute();

        return $"Ungrouped '{groupId}'. Restored {memberCount} component(s).";
    }

    /// <inheritdoc/>
    public string SaveGroupAsPrefab(string groupId, string prefabName, string? description = null)
    {
        var groupVm = _canvas.Components.FirstOrDefault(c => c.Component.Identifier == groupId);
        if (groupVm?.Component is not ComponentGroup group)
            return $"Component '{groupId}' is not a group.";

        var libraryVm = _leftPanel.ComponentLibrary;
        if (libraryVm == null)
            return "Component library not available.";

        var previewGenerator = new GroupPreviewGenerator();
        var cmd = new SaveGroupToLibraryCommand(libraryVm, previewGenerator, group, prefabName, description);
        cmd.Execute();

        return $"Saved group '{groupId}' as prefab '{prefabName}' in component library.";
    }

    /// <inheritdoc/>
    public string InspectGroup(string groupId)
    {
        var groupVm = _canvas.Components.FirstOrDefault(c => c.Component.Identifier == groupId);
        if (groupVm?.Component is not ComponentGroup group)
            return $"Component '{groupId}' is not a group or does not exist.";

        var result = new
        {
            group_id = group.Identifier,
            group_name = group.GroupName,
            description = group.Description,
            position = new { x = Math.Round(group.PhysicalX, 1), y = Math.Round(group.PhysicalY, 1) },
            child_components = group.ChildComponents.Select(child => new
            {
                id = child.Identifier,
                type = child.HumanReadableName ?? child.NazcaFunctionName ?? child.Identifier,
                position = new { x = Math.Round(child.PhysicalX, 1), y = Math.Round(child.PhysicalY, 1) },
                rotation = child.RotationDegrees,
                is_group = child is ComponentGroup
            }).ToList(),
            internal_connections = group.InternalPaths.Select(path => new
            {
                from = $"{path.StartPin?.ParentComponent?.Identifier}:{path.StartPin?.Name}",
                to = $"{path.EndPin?.ParentComponent?.Identifier}:{path.EndPin?.Name}"
            }).ToList(),
            external_pins = group.ExternalPins.Select(pin => new
            {
                name = pin.Name,
                angle_degrees = pin.AngleDegrees,
                relative_position = new { x = Math.Round(pin.RelativeX, 1), y = Math.Round(pin.RelativeY, 1) },
                maps_to_internal = $"{pin.InternalPin?.ParentComponent?.Identifier}:{pin.InternalPin?.Name}"
            }).ToList(),
            nested_group_count = group.ChildComponents.Count(c => c is ComponentGroup),
            total_child_count = group.ChildComponents.Count
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <inheritdoc/>
    public Task<string> CopyComponentAsync(string sourceId, double x, double y, int rotation = -1)
    {
        var sourceVm = _canvas.Components.FirstOrDefault(c =>
            c.Component.Identifier.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

        if (sourceVm == null)
            return Task.FromResult($"Component '{sourceId}' not found.");

        var tempClipboard = new ComponentClipboard { PdkSourceResolver = PlacementContext.ResolveComponentPdkSource };
        tempClipboard.Copy(new[] { sourceVm }, _canvas.Connections);

        // Single-process enforcement (issues #570/#653) — mirrors the paste gate;
        // PeekPdkSources expands groups to their resolved children.
        var guard = ResolveProcessGuard();
        var copyBlockReason = tempClipboard.PeekPdkSources()
            .Select(pdk => CheckProcess(pdk, guard))
            .Where(check => !check.IsAllowed)
            .Select(check => check.BlockReason)
            .FirstOrDefault();
        if (copyBlockReason != null)
            return Task.FromResult(copyBlockReason);

        var result = tempClipboard.Paste(_canvas, x, y);
        if (result == null || result.Components.Count == 0)
            return Task.FromResult($"Failed to copy '{sourceId}'.");

        var copiedVm = result.Components[0];
        var copiedId = copiedVm.Component.Identifier;

        if (rotation >= 0 && rotation != copiedVm.Component.RotationDegrees
            && copiedVm.Component is not ComponentGroup)
        {
            copiedVm.Component.RotationDegrees = rotation;
        }

        var px = Math.Round(copiedVm.Component.PhysicalX, 0);
        var py = Math.Round(copiedVm.Component.PhysicalY, 0);
        return Task.FromResult(
            $"Copied '{sourceId}' to ({px}, {py})µm. New ID: '{copiedId}'.");
    }

    /// <summary>
    /// Returns the effective pins for connecting to a component.
    /// For ComponentGroups, returns the InternalPins of ExternalPins so they can be wired up.
    /// </summary>
    private static IEnumerable<PhysicalPin> GetEffectivePins(Component component)
    {
        if (component is ComponentGroup group && group.ExternalPins.Count > 0)
            return group.ExternalPins.Select(ep => ep.InternalPin);

        return component.PhysicalPins;
    }

    private HashSet<PhysicalPin> GetConnectedPins() =>
        _canvas.Connections
            .SelectMany(c => new[] { c.Connection.StartPin, c.Connection.EndPin })
            .ToHashSet();

    /// <summary>
    /// Finds the best available pin — prefers output (0°/270°) or input (180°/90°) based on flag.
    /// Falls back to any unconnected pin if no directional match is found.
    /// </summary>
    private static PhysicalPin? FindBestPin(
        IEnumerable<PhysicalPin> pins,
        HashSet<PhysicalPin> alreadyConnected,
        bool preferOutput)
    {
        var available = pins.Where(p => !alreadyConnected.Contains(p)).ToList();
        if (available.Count == 0) return null;

        var preferred = preferOutput
            ? available.FirstOrDefault(p => p.AngleDegrees is 0 or 270)
            : available.FirstOrDefault(p => p.AngleDegrees is 180 or 90);

        return preferred ?? available.FirstOrDefault();
    }
}
