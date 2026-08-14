using CAP_Contracts;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.Grid;
using CAP_DataAccess.Persistence.DTOs;
using System.Text.Json;

namespace CAP_DataAccess.Persistence;

/// <summary>
/// Extends GridPersistenceManager to support saving and loading ComponentGroups.
/// Handles hierarchical structure, frozen paths, and parent-child relationships.
/// </summary>
public class GridPersistenceWithGroupsManager
{
    private readonly GridManager _gridManager;
    private readonly IDataAccessor _dataAccessor;

    public GridPersistenceWithGroupsManager(GridManager gridManager, IDataAccessor dataAccessor)
    {
        _gridManager = gridManager;
        _dataAccessor = dataAccessor;
    }

    /// <summary>
    /// Saves the grid including ComponentGroups to JSON.
    /// </summary>
    /// <param name="path">Path to save the file.</param>
    /// <returns>True if save succeeded.</returns>
    public async Task<bool> SaveAsync(string path)
    {
        var saveData = new GridSaveData
        {
            Components = new List<ComponentData>(),
            Groups = new List<ComponentGroupDto>()
        };

        var componentSet = new HashSet<Component>();
        var savedComponents = new HashSet<string>(); // Track which components we've saved

        // Collect all components and groups
        for (int x = 0; x < _gridManager.TileManager.Tiles.GetLength(0); x++)
        {
            for (int y = 0; y < _gridManager.TileManager.Tiles.GetLength(1); y++)
            {
                var tile = _gridManager.TileManager.Tiles[x, y];
                if (tile?.Component == null) continue;

                var component = tile.Component;
                if (x != component.GridXMainTile || y != component.GridYMainTile) continue;

                // Avoid duplicates
                if (componentSet.Contains(component)) continue;
                componentSet.Add(component);

                if (component is ComponentGroup group)
                {
                    // Save as group (and all nested groups recursively)
                    SaveGroupsRecursive(group, saveData.Groups);

                    // Also save all child components' data
                    SaveChildComponentsRecursive(group, saveData.Components, savedComponents);
                }
                else
                {
                    // Save as regular component (only if not already saved as part of a group)
                    if (!savedComponents.Contains(component.Identifier))
                    {
                        saveData.Components.Add(new ComponentData
                        {
                            Identifier = component.Identifier,
                            HumanReadableName = component.HumanReadableName,
                            Rotation = (int)component.Rotation90CounterClock,
                            RotationDegrees = ComponentPoseTransform.GetNonCardinalRotationDegrees(component),
                            Mirrored = component.IsMirroredHorizontally ? true : null,
                            Sliders = component.GetAllSliders(),
                            X = x,
                            Y = y
                        });
                        savedComponents.Add(component.Identifier);
                    }
                }
            }
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(saveData, options);
        return await _dataAccessor.Write(path, json);
    }

    /// <summary>
    /// Loads the grid including ComponentGroups from JSON.
    /// </summary>
    /// <param name="path">Path to the save file.</param>
    /// <param name="componentFactory">Factory to create components.</param>
    public async Task LoadAsync(string path, IComponentFactory componentFactory)
    {
        var json = _dataAccessor.ReadAsText(path);

        GridSaveData? saveData;
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            saveData = JsonSerializer.Deserialize<GridSaveData>(json, options);
        }
        catch
        {
            // Fallback to old format (just components, no groups)
            var oldFormat = JsonSerializer.Deserialize<List<ComponentData>>(json);
            saveData = new GridSaveData
            {
                Components = oldFormat ?? new List<ComponentData>(),
                Groups = new List<ComponentGroupDto>()
            };
        }

        if (saveData == null)
            throw new InvalidOperationException("Failed to deserialize save data.");

        _gridManager.ComponentMover.DeleteAllComponents();

        // First, create all components (both standalone and those in groups)
        var componentLookup = new Dictionary<string, Component>();

        // Collect all child component IDs from groups
        var childComponentIds = new HashSet<string>();
        foreach (var groupDto in saveData.Groups)
        {
            foreach (var childId in groupDto.ChildComponentIds)
            {
                childComponentIds.Add(childId);
            }
        }

        // Load standalone components (not in any group)
        foreach (var data in saveData.Components)
        {
            if (!childComponentIds.Contains(data.Identifier))
            {
                // Extract template name from identifier (e.g., "MMI_1x2_1" -> "MMI_1x2")
                var templateName = ExtractTemplateName(data.Identifier);
                var component = componentFactory.CreateComponentByIdentifier(templateName);

                // CRITICAL: Restore the saved Identifier to maintain uniqueness and readable naming
                component.Identifier = data.Identifier;

                // CRITICAL: Restore HumanReadableName if saved (for backward compatibility, fall back to Identifier)
                component.HumanReadableName = data.HumanReadableName ?? data.Identifier;

                RestorePose(data, component);
                LoadSliders(data, component);
                _gridManager.ComponentMover.PlaceComponent(data.X, data.Y, component);
                componentLookup[component.Identifier] = component;
            }
        }

        // Create all child components for groups (they won't be placed in grid yet)
        foreach (var data in saveData.Components)
        {
            if (childComponentIds.Contains(data.Identifier))
            {
                // Extract template name from identifier (e.g., "MMI_1x2_1" -> "MMI_1x2")
                var templateName = ExtractTemplateName(data.Identifier);
                var component = componentFactory.CreateComponentByIdentifier(templateName);

                // CRITICAL: Restore the saved Identifier to maintain uniqueness and readable naming
                component.Identifier = data.Identifier;

                // CRITICAL: Restore HumanReadableName if saved (for backward compatibility, fall back to Identifier)
                component.HumanReadableName = data.HumanReadableName ?? data.Identifier;

                RestorePose(data, component);
                LoadSliders(data, component);
                // Don't place in grid - they'll be children of groups
                componentLookup[component.Identifier] = component;
            }
        }

        // Load groups in order (leaf groups first, then parents)
        var groupDtos = SortGroupsByHierarchy(saveData.Groups);

        foreach (var groupDto in groupDtos)
        {
            var group = ComponentGroupSerializer.FromDto(groupDto, componentLookup);
            componentLookup[group.Identifier] = group;

            // Only place top-level groups (those without a parent) on the grid
            // Nested groups are already added as children through ComponentGroupSerializer.FromDto
            if (groupDto.ParentGroupId == null)
            {
                _gridManager.ComponentMover.PlaceComponent(groupDto.GridX, groupDto.GridY, group);
            }
        }

        // After all groups are loaded, establish parent-child relationships for nested groups
        foreach (var groupDto in saveData.Groups.Where(g => g.ParentGroupId != null))
        {
            if (componentLookup.TryGetValue(groupDto.Identifier, out var childGroup) &&
                componentLookup.TryGetValue(groupDto.ParentGroupId!, out var parentComp))
            {
                if (parentComp is ComponentGroup parent)
                {
                    childGroup.ParentGroup = parent;
                    if (childGroup is ComponentGroup childGroupTyped)
                    {
                        childGroupTyped.ParentGroup = parent;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sorts groups so that leaf groups (no group children) are loaded first.
    /// This ensures parent-child relationships can be established correctly.
    /// </summary>
    private List<ComponentGroupDto> SortGroupsByHierarchy(List<ComponentGroupDto> groups)
    {
        var sorted = new List<ComponentGroupDto>();
        var remaining = new HashSet<ComponentGroupDto>(groups);
        var groupIdToDto = groups.ToDictionary(g => g.Identifier);

        // Iteratively add groups that have all their child groups already added
        while (remaining.Count > 0)
        {
            var canAdd = remaining.Where(g =>
            {
                // Check if all child groups are already in sorted
                var childGroupIds = g.ChildComponentIds
                    .Where(id => groupIdToDto.ContainsKey(id))
                    .ToList();

                return childGroupIds.All(id => sorted.Any(s => s.Identifier == id));
            }).ToList();

            if (canAdd.Count == 0 && remaining.Count > 0)
            {
                // Circular dependency or orphaned groups - just add the rest
                sorted.AddRange(remaining);
                break;
            }

            foreach (var group in canAdd)
            {
                sorted.Add(group);
                remaining.Remove(group);
            }
        }

        return sorted;
    }

    /// <summary>
    /// Recursively saves all child components of a group.
    /// </summary>
    /// <summary>
    /// Recursively saves a ComponentGroup and all nested groups to the Groups list.
    /// </summary>
    private void SaveGroupsRecursive(ComponentGroup group, List<ComponentGroupDto> groups)
    {
        // Save this group
        groups.Add(ComponentGroupSerializer.ToDto(group));

        // Recursively save any nested groups
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                SaveGroupsRecursive(childGroup, groups);
            }
        }
    }

    /// <summary>
    /// Recursively saves all regular components (non-groups) within a ComponentGroup to the Components list.
    /// </summary>
    private void SaveChildComponentsRecursive(
        ComponentGroup group,
        List<ComponentData> components,
        HashSet<string> savedComponents)
    {
        foreach (var child in group.ChildComponents)
        {
            // Skip ComponentGroup children - they are saved in the Groups list, not Components list
            if (child is ComponentGroup childGroup)
            {
                SaveChildComponentsRecursive(childGroup, components, savedComponents);
            }
            else if (!savedComponents.Contains(child.Identifier))
            {
                // Only save regular components (not groups) to the Components list
                components.Add(new ComponentData
                {
                    Identifier = child.Identifier,
                    HumanReadableName = child.HumanReadableName,
                    Rotation = (int)child.Rotation90CounterClock,
                    RotationDegrees = ComponentPoseTransform.GetNonCardinalRotationDegrees(child),
                    Mirrored = child.IsMirroredHorizontally ? true : null,
                    Sliders = child.GetAllSliders(),
                    X = child.GridXMainTile,
                    Y = child.GridYMainTile
                });
                savedComponents.Add(child.Identifier);
            }
        }
    }

    /// <summary>
    /// Extracts the template name from a component identifier.
    /// Examples:
    ///   "MMI_1x2_1" -> "MMI_1x2"
    ///   "MMI_1x2_2" -> "MMI_1x2"
    ///   "MMI_1x2" -> "MMI_1x2" (unchanged if no copy suffix)
    /// </summary>
    private static string ExtractTemplateName(string identifier)
    {
        // Try to find template name by removing the copy number suffix
        // Pattern: "TemplateName_N" where N is a number
        var lastUnderscore = identifier.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            var suffix = identifier.Substring(lastUnderscore + 1);
            // Check if suffix is a number (indicating a copy, e.g., "_1", "_2")
            if (int.TryParse(suffix, out _))
            {
                return identifier.Substring(0, lastUnderscore);
            }
        }

        // No copy suffix found - return as-is
        return identifier;
    }

    /// <summary>
    /// Restores a component's saved pose: mirror first (local, unrotated frame),
    /// then the discrete quarter turns, then the exact continuous angle when the
    /// file carries one (GDS-imported non-cardinal instance; null in older files
    /// and for cardinal rotations).
    /// </summary>
    private static void RestorePose(ComponentData data, Component component)
    {
        if (data.Mirrored == true)
            ComponentPoseTransform.MirrorPinsHorizontally(component);

        component.Rotation90CounterClock = (DiscreteRotation)data.Rotation;

        if (data.RotationDegrees is double exact)
            ComponentPoseTransform.ApplyExactRotation(component, exact);
    }

    /// <summary>
    /// Loads sliders from saved data into a component.
    /// </summary>
    private static void LoadSliders(ComponentData data, Component component)
    {
        if (data.Sliders != null)
        {
            foreach (var sliderToLoad in data.Sliders)
            {
                var predefinedSlider = component.GetSlider(sliderToLoad.Number);
                if (predefinedSlider == null)
                {
                    component.AddSlider(sliderToLoad.Number, sliderToLoad);
                }
                else
                {
                    predefinedSlider.Value = sliderToLoad.Value;
                }
            }
        }
    }

    /// <summary>
    /// Root data structure for saving grid with components and groups.
    /// </summary>
    public class GridSaveData
    {
        /// <summary>
        /// Regular components (not groups).
        /// </summary>
        public List<ComponentData> Components { get; set; } = new();

        /// <summary>
        /// ComponentGroups with their hierarchical structure.
        /// </summary>
        public List<ComponentGroupDto> Groups { get; set; } = new();
    }

    /// <summary>
    /// Data for a regular component.
    /// </summary>
    public class ComponentData
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Rotation { get; set; }

        /// <summary>
        /// Exact continuous rotation in degrees for non-cardinal placements (GDS
        /// import). Null in old files and for cardinal rotations — <see cref="Rotation"/>
        /// alone restores those.
        /// </summary>
        public double? RotationDegrees { get; set; }

        /// <summary>
        /// True when the pins were mirrored across the local horizontal centerline
        /// (GDS STRANS-reflected instance). Null in old files — no mirror.
        /// </summary>
        public bool? Mirrored { get; set; }

        public string Identifier { get; set; } = "";
        public string? HumanReadableName { get; set; }
        public List<Slider>? Sliders { get; set; }
    }
}
