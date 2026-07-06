using System.Linq;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;

namespace CAP.Avalonia.Selection;

/// <summary>
/// Stores copied component data for paste operations.
/// Captures component templates and internal connections between copied components.
/// </summary>
public class ComponentClipboard
{
    /// <summary>
    /// Offset applied to pasted components relative to original positions.
    /// </summary>
    private const double PasteOffsetMicrometers = 50.0;

    private List<ClipboardEntry> _entries = new();
    private List<ClipboardConnection> _internalConnections = new();

    /// <summary>
    /// Whether the clipboard has content to paste.
    /// </summary>
    public bool HasContent => _entries.Count > 0;

    /// <summary>
    /// Resolves a component's PDK source from the loaded library when the ViewModel-level
    /// <c>TemplatePdkSource</c> is missing (pasted copies, group children, undo-recreated
    /// VMs). Wired by <c>MainViewModel</c>; null keeps the previous VM-only behaviour.
    /// </summary>
    public Func<Component, string?>? PdkSourceResolver { get; set; }

    /// <summary>
    /// PDK sources of every copied LEAF component, in copy order (issue #570). Groups are
    /// expanded to their children — a group VM itself carries no PDK source, and treating
    /// it as one entry made foreign-PDK components paste into a locked design unchecked.
    /// Lets callers (e.g. <c>CanvasInteractionViewModel.PasteSelected</c>) check the
    /// active-process policy against clipboard contents before executing the paste command.
    /// </summary>
    public IReadOnlyList<string?> PeekPdkSources() =>
        _entries.SelectMany(e => e.OriginalComponent is ComponentGroup group
                ? Flatten(group).Select(child => PdkSourceResolver?.Invoke(child))
                : new[] { e.PdkSource ?? PdkSourceResolver?.Invoke(e.OriginalComponent) })
            .ToList();

    private static IEnumerable<Component> Flatten(ComponentGroup group) =>
        group.ChildComponents.SelectMany(c =>
            c is ComponentGroup nested ? Flatten(nested) : new[] { c });

    /// <summary>
    /// Copies the given components and their internal connections.
    /// </summary>
    public void Copy(
        IReadOnlyList<ComponentViewModel> components,
        IEnumerable<WaveguideConnectionViewModel> allConnections)
    {
        _entries.Clear();
        _internalConnections.Clear();

        var componentSet = new HashSet<Component>(
            components.Select(c => c.Component));

        foreach (var comp in components)
        {
            _entries.Add(new ClipboardEntry(
                comp.Component,
                comp.TemplateName,
                comp.X,
                comp.Y,
                // Pasted copies, undo-recreated VMs and ungrouped children legitimately
                // lack TemplatePdkSource — fall back to the library lookup so process
                // enforcement never sees a false "built-in" (issue #570).
                comp.TemplatePdkSource ?? PdkSourceResolver?.Invoke(comp.Component)));
        }

        // Capture internal connections (both endpoints inside selection)
        foreach (var conn in allConnections)
        {
            var startComp = conn.Connection.StartPin.ParentComponent;
            var endComp = conn.Connection.EndPin.ParentComponent;

            if (componentSet.Contains(startComp) && componentSet.Contains(endComp))
            {
                int startIdx = components.ToList().FindIndex(
                    c => c.Component == startComp);
                int endIdx = components.ToList().FindIndex(
                    c => c.Component == endComp);

                _internalConnections.Add(new ClipboardConnection(
                    startIdx,
                    conn.Connection.StartPin.Name,
                    endIdx,
                    conn.Connection.EndPin.Name));
            }
        }
    }

    /// <summary>
    /// Pastes copied components onto the canvas.
    /// If targetX/targetY are provided, pastes relative to cursor position.
    /// Otherwise pastes with fixed offset from original positions.
    /// Returns the list of newly created component ViewModels.
    /// </summary>
    public PasteResult? Paste(DesignCanvasViewModel canvas, double? targetX = null, double? targetY = null)
    {
        if (!HasContent) return null;

        var newComponents = new List<ComponentViewModel>();
        var clonedComps = new List<Component>();

        // Maps each source component's original Identifier to its clone's new Identifier
        // (incl. group children), so identifier-keyed state like Nazca overrides can follow the copy.
        var pastedIdentifierMap = new Dictionary<string, string>();

        // Get existing component names for unique name generation.
        // Include child identifiers from all groups so copy names don't collide.
        var existingNames = canvas.Components
            .Select(c => c.Component.Identifier)
            .ToList();
        foreach (var comp in canvas.Components)
        {
            if (comp.Component is ComponentGroup grp)
                CollectAllChildIdentifiers(grp, existingNames);
        }

        // Calculate offset: either from cursor position or fixed offset
        double offsetX, offsetY;
        if (targetX.HasValue && targetY.HasValue)
        {
            // Paste at cursor - calculate offset from first component's original position
            var firstEntry = _entries[0];
            offsetX = targetX.Value - firstEntry.OriginalX;
            offsetY = targetY.Value - firstEntry.OriginalY;
        }
        else
        {
            // Paste with fixed offset
            offsetX = PasteOffsetMicrometers;
            offsetY = PasteOffsetMicrometers;
        }

        foreach (var entry in _entries)
        {
            var cloned = (Component)entry.OriginalComponent.Clone();

            // Generate readable name for pasted component
            if (cloned is ComponentGroup group)
            {
                // For groups, generate readable Identifier for the group itself
                var newGroupIdentifier = ComponentNameGenerator.GenerateGroupName(
                    group.GroupName,
                    existingNames);
                group.Identifier = newGroupIdentifier;

                // Also set HumanReadableName for UI display
                group.HumanReadableName = newGroupIdentifier;

                // Recursively rename child components to have readable names
                // This creates a mapping of old -> new identifiers
                var identifierMap = new Dictionary<string, Component>();
                RenameGroupChildren(group, existingNames, identifierMap);

                // Update ExternalPin references to use the renamed child components
                UpdateExternalPinReferences(group, identifierMap);

                // Record old->new identifiers for the group itself and every child.
                // Pair original<->clone children by traversal order (robust even though
                // Clone() regenerates child identifiers), so identifier-keyed state maps correctly.
                pastedIdentifierMap[entry.OriginalComponent.Identifier] = group.Identifier;
                MapGroupChildIdentifiers((ComponentGroup)entry.OriginalComponent, group, pastedIdentifierMap);
            }
            else
            {
                // For regular components, generate incremental name
                var newIdentifier = ComponentNameGenerator.GenerateCopyName(
                    entry.OriginalComponent.Identifier,
                    existingNames);
                cloned.Identifier = newIdentifier;
                pastedIdentifierMap[entry.OriginalComponent.Identifier] = newIdentifier;

                // Also update HumanReadableName to match the new copy
                if (entry.OriginalComponent.HumanReadableName != null)
                {
                    cloned.HumanReadableName = ComponentNameGenerator.GenerateCopyName(
                        entry.OriginalComponent.HumanReadableName,
                        existingNames);
                }
            }

            // Add to list so subsequent components see it
            existingNames.Add(cloned.Identifier);

            double newX = entry.OriginalX + offsetX;
            double newY = entry.OriginalY + offsetY;

            // For ComponentGroups, use MoveGroup to move the entire group (children, pins, paths)
            if (cloned is ComponentGroup groupToMove)
            {
                double deltaX = newX - groupToMove.PhysicalX;
                double deltaY = newY - groupToMove.PhysicalY;
                groupToMove.MoveGroup(deltaX, deltaY);
            }
            else
            {
                cloned.PhysicalX = newX;
                cloned.PhysicalY = newY;
            }

            clonedComps.Add(cloned);
            // Thread the PDK source onto the new VM — dropping it here made every
            // pasted component read as "built-in" and bypass process enforcement (#570).
            var vm = canvas.AddComponent(cloned, entry.TemplateName, entry.PdkSource);
            newComponents.Add(vm);
        }

        // Reconnect internal connections
        var newConnections = new List<WaveguideConnectionViewModel>();
        foreach (var conn in _internalConnections)
        {
            if (conn.StartComponentIndex >= newComponents.Count ||
                conn.EndComponentIndex >= newComponents.Count)
                continue;

            var startComp = newComponents[conn.StartComponentIndex].Component;
            var endComp = newComponents[conn.EndComponentIndex].Component;

            var startPin = startComp.PhysicalPins
                .FirstOrDefault(p => p.Name == conn.StartPinName);
            var endPin = endComp.PhysicalPins
                .FirstOrDefault(p => p.Name == conn.EndPinName);

            if (startPin != null && endPin != null)
            {
                var connVm = canvas.ConnectPins(startPin, endPin);
                if (connVm != null)
                    newConnections.Add(connVm);
            }
        }

        return new PasteResult(newComponents, newConnections, pastedIdentifierMap);
    }

    /// <summary>
    /// Recursively maps each original group child's identifier to its pasted clone's
    /// identifier by pairing children in traversal order. Used to carry identifier-keyed
    /// per-instance state (Nazca overrides) onto pasted group children.
    /// </summary>
    private static void MapGroupChildIdentifiers(
        ComponentGroup original, ComponentGroup clone, Dictionary<string, string> map)
    {
        int count = Math.Min(original.ChildComponents.Count, clone.ChildComponents.Count);
        for (int i = 0; i < count; i++)
        {
            var originalChild = original.ChildComponents[i];
            var clonedChild = clone.ChildComponents[i];
            map[originalChild.Identifier] = clonedChild.Identifier;

            if (originalChild is ComponentGroup originalSub && clonedChild is ComponentGroup clonedSub)
                MapGroupChildIdentifiers(originalSub, clonedSub, map);
        }
    }

    /// <summary>
    /// Recursively collects all child component identifiers from a group hierarchy.
    /// Used to ensure pasted copy names don't collide with existing group children.
    /// </summary>
    private static void CollectAllChildIdentifiers(ComponentGroup group, List<string> names)
    {
        foreach (var child in group.ChildComponents)
        {
            names.Add(child.Identifier);
            if (child is ComponentGroup nested)
                CollectAllChildIdentifiers(nested, names);
        }
    }

    /// <summary>
    /// Recursively renames child components within a group to have readable Names.
    /// Builds a mapping of old Identifiers to new Component references for ExternalPin updates.
    /// Note: Each component gets a fresh Id (Guid) automatically via Clone(), so Id collisions are impossible.
    /// </summary>
    private void RenameGroupChildren(ComponentGroup group, List<string> existingNames, Dictionary<string, Component> identifierMap)
    {
        foreach (var child in group.ChildComponents)
        {
            var oldIdentifier = child.Identifier;

            if (child is ComponentGroup childGroup)
            {
                // Recursively handle nested groups
                var newChildGroupIdentifier = ComponentNameGenerator.GenerateGroupName(
                    childGroup.GroupName,
                    existingNames);
                childGroup.Identifier = newChildGroupIdentifier;
                childGroup.HumanReadableName = newChildGroupIdentifier;
                existingNames.Add(newChildGroupIdentifier);
                identifierMap[oldIdentifier] = childGroup;
                RenameGroupChildren(childGroup, existingNames, identifierMap);
            }
            else
            {
                // Generate readable name for child component
                // Use the original name (strip GUID suffix from Identifier)
                var baseName = ComponentNameGenerator.GenerateCopyName(
                    ExtractBaseNameFromIdentifier(child.Identifier),
                    existingNames);
                child.Identifier = baseName;
                child.HumanReadableName = baseName;
                existingNames.Add(baseName);
                identifierMap[oldIdentifier] = child;
            }
        }
    }

    /// <summary>
    /// Updates ExternalPin references after child components have been renamed.
    /// ExternalPins point to internal PhysicalPins via ParentComponent.Identifier lookups.
    /// </summary>
    private void UpdateExternalPinReferences(ComponentGroup group, Dictionary<string, Component> identifierMap)
    {
        // ExternalPins already have direct references to PhysicalPin objects (not Identifier strings)
        // So they should still work after renaming. But let's verify they point to the right components.
        foreach (var externalPin in group.ExternalPins)
        {
            // The InternalPin.ParentComponent should already be correctly set from DeepCopy()
            // No action needed - the object reference is still valid after renaming
        }

        // Recursively update nested groups
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                UpdateExternalPinReferences(childGroup, identifierMap);
            }
        }
    }

    /// <summary>
    /// Extracts base name by removing GUID suffix from Identifier.
    /// Example: "MMI_1x2_abc123def456" -> "MMI_1x2"
    /// </summary>
    private static string ExtractBaseNameFromIdentifier(string identifier)
    {
        // Remove GUID suffix (32 hex chars)
        var lastUnderscore = identifier.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            var suffix = identifier.Substring(lastUnderscore + 1);
            // Check if it's a GUID (all hex chars, length 32)
            if (suffix.Length == 32 && suffix.All(c => "0123456789abcdefABCDEF".Contains(c)))
            {
                return identifier.Substring(0, lastUnderscore);
            }
        }
        return identifier;
    }

    /// <summary>
    /// Data stored for a single copied component.
    /// </summary>
    private sealed record ClipboardEntry(
        Component OriginalComponent,
        string? TemplateName,
        double OriginalX,
        double OriginalY,
        string? PdkSource);

    /// <summary>
    /// Data stored for an internal connection between copied components.
    /// </summary>
    private sealed record ClipboardConnection(
        int StartComponentIndex,
        string StartPinName,
        int EndComponentIndex,
        string EndPinName);
}

/// <summary>
/// Result of a paste operation containing newly created components and connections.
/// </summary>
/// <param name="Components">The newly created component ViewModels.</param>
/// <param name="Connections">The newly created internal connections.</param>
/// <param name="IdentifierMap">
/// Maps each pasted source component's original <c>Identifier</c> to its clone's new
/// <c>Identifier</c> (groups include an entry per renamed child). Used to carry
/// per-instance state keyed by identifier — e.g. Nazca raw-code overrides — onto the copies.
/// </param>
public sealed record PasteResult(
    List<ComponentViewModel> Components,
    List<WaveguideConnectionViewModel> Connections,
    IReadOnlyDictionary<string, string> IdentifierMap);
