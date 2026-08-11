using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;

namespace CAP_Core.Components.Core;

/// <summary>
/// Represents a hierarchical group of components with frozen waveguide paths.
/// Follows IPKISS-style transparent hierarchy where groups contain child components
/// at fixed relative positions with frozen waveguide paths.
/// </summary>
public class ComponentGroup : Component, INotifyPropertyChanged
{
    /// <summary>
    /// Child components contained in this group with relative positions.
    /// </summary>
    public List<Component> ChildComponents { get; private set; } = new();

    /// <summary>
    /// Frozen waveguide paths between child components (don't recalculate during group moves).
    /// May also carry pin-less geometry (GDS-imported route outlines — see
    /// <see cref="FrozenWaveguidePath.StartPin"/>).
    /// </summary>
    public List<FrozenWaveguidePath> InternalPaths { get; private set; } = new();

    /// <summary>
    /// External pins exposed by this group for connections to outside components.
    /// </summary>
    public List<GroupPin> ExternalPins { get; private set; } = new();

    /// <summary>
    /// Human-readable name for this group.
    /// </summary>
    private string _groupName = "";
    public string GroupName
    {
        get => _groupName;
        set
        {
            if (_groupName != value)
            {
                _groupName = value;
                OnPropertyChanged();
                UpdateLabelBoundsAfterNameChange();
            }
        }
    }

    /// <summary>
    /// Optional description of this group's purpose.
    /// </summary>
    private string _description = "";
    public string Description
    {
        get => _description;
        set
        {
            if (_description != value)
            {
                _description = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Event raised when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Indicates whether this group is saved as a reusable prefab/template in the library.
    /// Only prefabs appear in the "Saved Groups" panel.
    /// </summary>
    public bool IsPrefab { get; set; }

    /// <summary>
    /// Reference to parent group if this group is nested within another group.
    /// Null if this is a top-level group.
    /// </summary>
    [JsonIgnore]
    public new ComponentGroup? ParentGroup { get; set; }

    /// <summary>
    /// Bounding rectangle for the group label (used for hit testing).
    /// Updated when the group bounds change.
    /// </summary>
    [JsonIgnore]
    public (double X, double Y, double Width, double Height) LabelBounds { get; private set; }

    /// <summary>
    /// Lazy-initialized S-Matrix builder for computing group S-Matrices.
    /// </summary>
    private ComponentGroupSMatrixBuilder? _sMatrixBuilder;

    /// <summary>
    /// Offset from PhysicalX to the minimum X coordinate of child components.
    /// Used by BoundingBoxCalculator to correctly position the group's bounding box.
    /// </summary>
    [JsonIgnore]
    public double MinChildOffsetX { get; private set; }

    /// <summary>
    /// Offset from PhysicalY to the minimum Y coordinate of child components.
    /// Used by BoundingBoxCalculator to correctly position the group's bounding box.
    /// </summary>
    [JsonIgnore]
    public double MinChildOffsetY { get; private set; }

    /// <summary>
    /// Creates an empty ComponentGroup with default S-matrices.
    /// Use AddChild() and AddInternalPath() to populate the group.
    /// </summary>
    public ComponentGroup(string groupName = "Unnamed Group")
        : base(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "group",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            $"group_{Guid.NewGuid():N}",
            new DiscreteRotation(),
            new List<PhysicalPin>()
        )
    {
        _groupName = groupName;
        _description = "";
    }

    /// <summary>
    /// Adds a child component to this group.
    /// The child's position is stored as relative to the group origin.
    /// </summary>
    /// <param name="component">Component to add to the group.</param>
    public void AddChild(Component component)
    {
        if (component == null)
            throw new ArgumentNullException(nameof(component));

        if (ChildComponents.Contains(component))
            throw new InvalidOperationException("Component is already a child of this group.");

        component.ParentGroup = this;

        // If the component is itself a group, also set its typed ParentGroup property
        if (component is ComponentGroup childGroup)
        {
            childGroup.ParentGroup = this;
        }

        ChildComponents.Add(component);
        UpdateGroupBounds();
        InvalidateSMatrix();
    }

    /// <summary>
    /// Adds multiple child components in one batch, recomputing the group bounds
    /// and invalidating the S-Matrix only once. Per-item <see cref="AddChild"/>
    /// rescans every child and path segment on each call, which is quadratic at
    /// GDS-import scale (hundreds of children and segment-rich frozen paths).
    /// </summary>
    /// <param name="components">Components to add; none may already be a child.</param>
    public void AddChildren(IReadOnlyCollection<Component> components)
    {
        if (components == null)
            throw new ArgumentNullException(nameof(components));
        if (components.Count == 0)
            return;

        var existing = new HashSet<Component>(ChildComponents);
        foreach (var component in components)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(components), "Collection contains a null component.");
            if (!existing.Add(component))
                throw new InvalidOperationException("Component is already a child of this group.");

            component.ParentGroup = this;
            if (component is ComponentGroup childGroup)
                childGroup.ParentGroup = this;
            ChildComponents.Add(component);
        }

        UpdateGroupBounds();
        InvalidateSMatrix();
    }

    /// <summary>
    /// Removes a child component from this group.
    /// </summary>
    /// <param name="component">Component to remove.</param>
    /// <returns>True if the component was removed, false if it wasn't in the group.</returns>
    public bool RemoveChild(Component component)
    {
        if (component == null) return false;

        bool removed = ChildComponents.Remove(component);
        if (removed)
        {
            component.ParentGroup = null;
            UpdateGroupBounds();
        }
        return removed;
    }

    /// <summary>
    /// Removes multiple child components in one batch, recomputing the group
    /// bounds only once (see <see cref="AddChildren"/> for why per-item calls
    /// are quadratic at GDS-import scale).
    /// </summary>
    /// <param name="components">Components to remove; non-children are ignored.</param>
    public void RemoveChildren(IReadOnlyCollection<Component> components)
    {
        if (components == null || components.Count == 0)
            return;

        var toRemove = new HashSet<Component>(components);
        bool removedAny = false;
        for (int i = ChildComponents.Count - 1; i >= 0; i--)
        {
            if (!toRemove.Contains(ChildComponents[i]))
                continue;

            ChildComponents[i].ParentGroup = null;
            ChildComponents.RemoveAt(i);
            removedAny = true;
        }

        if (removedAny)
            UpdateGroupBounds();
    }

    /// <summary>
    /// Adds a frozen waveguide path between two child components, or pin-less
    /// frozen geometry (a GDS-imported route outline).
    /// </summary>
    /// <param name="path">The frozen path to add.</param>
    public void AddInternalPath(FrozenWaveguidePath path)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));

        InternalPaths.Add(path);
        UpdateGroupBounds();
        InvalidateSMatrix();
    }

    /// <summary>
    /// Adds multiple frozen waveguide paths in one batch, recomputing the group
    /// bounds and invalidating the S-Matrix only once (see <see cref="AddChildren"/>
    /// for why per-item calls are quadratic at GDS-import scale).
    /// </summary>
    /// <param name="paths">The frozen paths to add.</param>
    public void AddInternalPaths(IReadOnlyCollection<FrozenWaveguidePath> paths)
    {
        if (paths == null)
            throw new ArgumentNullException(nameof(paths));
        if (paths.Count == 0)
            return;

        foreach (var path in paths)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(paths), "Collection contains a null path.");
            InternalPaths.Add(path);
        }

        UpdateGroupBounds();
        InvalidateSMatrix();
    }

    /// <summary>
    /// Removes a frozen waveguide path from this group.
    /// </summary>
    /// <param name="path">The path to remove.</param>
    /// <returns>True if the path was removed.</returns>
    public bool RemoveInternalPath(FrozenWaveguidePath path)
    {
        bool removed = InternalPaths.Remove(path);
        if (removed)
        {
            UpdateGroupBounds();
        }
        return removed;
    }

    /// <summary>
    /// Adds an external pin that exposes an internal component's pin.
    /// </summary>
    /// <param name="pin">The group pin to add.</param>
    public void AddExternalPin(GroupPin pin)
    {
        if (pin == null)
            throw new ArgumentNullException(nameof(pin));

        ExternalPins.Add(pin);
        InvalidateSMatrix();
    }

    /// <summary>
    /// Moves the entire group and all children by the specified delta.
    /// Internal waveguide paths are translated, not re-routed.
    /// </summary>
    /// <param name="deltaX">X offset in micrometers.</param>
    /// <param name="deltaY">Y offset in micrometers.</param>
    public void MoveGroup(double deltaX, double deltaY)
    {
        // Move group origin
        PhysicalX += deltaX;
        PhysicalY += deltaY;

        // Move all children (they use absolute positions that need to be offset)
        foreach (var child in ChildComponents)
        {
            child.PhysicalX += deltaX;
            child.PhysicalY += deltaY;

            // If child is also a group, recursively move its descendants
            if (child is ComponentGroup childGroup)
            {
                // Recursively move all descendants of the child group
                MoveChildrenRecursively(childGroup, deltaX, deltaY);
            }
        }

        // Translate frozen paths without re-routing
        foreach (var path in InternalPaths)
        {
            path.TranslateBy(deltaX, deltaY);
        }

        // Update label bounds after moving
        var (labelX, labelY, labelWidth, labelHeight) = LabelBounds;
        LabelBounds = (labelX + deltaX, labelY + deltaY, labelWidth, labelHeight);
    }

    /// <summary>
    /// Recursively moves all children of a group without moving the group itself.
    /// Used when a nested group's position has already been updated.
    /// </summary>
    private void MoveChildrenRecursively(ComponentGroup group, double deltaX, double deltaY)
    {
        foreach (var child in group.ChildComponents)
        {
            child.PhysicalX += deltaX;
            child.PhysicalY += deltaY;

            if (child is ComponentGroup childGroup)
            {
                MoveChildrenRecursively(childGroup, deltaX, deltaY);
            }
        }

        // Also translate the group's internal paths
        foreach (var path in group.InternalPaths)
        {
            path.TranslateBy(deltaX, deltaY);
        }
    }

    /// <summary>
    /// Rotates the entire group and all children by 90 degrees counter-clockwise.
    /// Updates child positions and frozen path geometries.
    /// </summary>
    public void RotateGroupBy90CounterClockwise()
    {
        // Rotate the group itself
        RotateBy90CounterClockwise();

        double groupX = PhysicalX;
        double groupY = PhysicalY;

        // Rotate each child around the group origin
        foreach (var child in ChildComponents)
        {
            // Calculate relative position
            double relX = child.PhysicalX - groupX;
            double relY = child.PhysicalY - groupY;

            // Rotate 90° counter-clockwise: (x, y) → (-y, x)
            double newRelX = -relY;
            double newRelY = relX;

            // Update child position
            child.PhysicalX = groupX + newRelX;
            child.PhysicalY = groupY + newRelY;

            // Rotate the child component itself
            child.RotateBy90CounterClockwise();
        }

        // Rotate frozen paths (each segment needs rotation around group origin)
        foreach (var frozenPath in InternalPaths)
        {
            RotatePathSegmentsBy90(frozenPath, groupX, groupY);
        }

        UpdateGroupBounds();
    }

    /// <summary>
    /// Rotates all segments in a frozen path by 90 degrees counter-clockwise around a center point.
    /// </summary>
    private void RotatePathSegmentsBy90(FrozenWaveguidePath frozenPath, double centerX, double centerY)
    {
        if (frozenPath?.Path?.Segments == null) return;

        foreach (var segment in frozenPath.Path.Segments)
        {
            segment.StartPoint = RotatePoint(segment.StartPoint.X, segment.StartPoint.Y, centerX, centerY);
            segment.EndPoint = RotatePoint(segment.EndPoint.X, segment.EndPoint.Y, centerX, centerY);

            if (segment is BendSegment bend)
            {
                bend.Center = RotatePoint(bend.Center.X, bend.Center.Y, centerX, centerY);
            }
        }
    }

    /// <summary>
    /// Rotates a point by 90 degrees counter-clockwise around a center.
    /// </summary>
    private (double X, double Y) RotatePoint(double x, double y, double centerX, double centerY)
    {
        double relX = x - centerX;
        double relY = y - centerY;

        // Rotate 90° counter-clockwise: (x, y) → (-y, x)
        double newRelX = -relY;
        double newRelY = relX;

        return (centerX + newRelX, centerY + newRelY);
    }

    /// <summary>
    /// Updates the group's bounding box based on child components and frozen paths.
    /// Also updates the label bounds for hit testing.
    /// </summary>
    public void UpdateGroupBounds()
    {
        if (ChildComponents.Count == 0 && InternalPaths.Count == 0)
        {
            WidthMicrometers = 0;
            HeightMicrometers = 0;
            LabelBounds = (PhysicalX, PhysicalY, 0, 0);
            return;
        }

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        // Consider child components
        foreach (var child in ChildComponents)
        {
            minX = Math.Min(minX, child.PhysicalX);
            minY = Math.Min(minY, child.PhysicalY);
            maxX = Math.Max(maxX, child.PhysicalX + child.WidthMicrometers);
            maxY = Math.Max(maxY, child.PhysicalY + child.HeightMicrometers);
        }

        // Consider frozen path segments
        foreach (var frozenPath in InternalPaths)
        {
            if (frozenPath?.Path?.Segments == null) continue;

            foreach (var segment in frozenPath.Path.Segments)
            {
                var bounds = GetSegmentBounds(segment);
                minX = Math.Min(minX, bounds.MinX);
                minY = Math.Min(minY, bounds.MinY);
                maxX = Math.Max(maxX, bounds.MaxX);
                maxY = Math.Max(maxY, bounds.MaxY);
            }
        }

        // If still at default values (no children or paths), reset to zero
        if (minX == double.MaxValue)
        {
            WidthMicrometers = 0;
            HeightMicrometers = 0;
            LabelBounds = (PhysicalX, PhysicalY, 0, 0);
            return;
        }

        WidthMicrometers = maxX - minX;
        HeightMicrometers = maxY - minY;

        // Store the minimum corner offsets for bounding box calculation
        // These are used by BoundingBoxCalculator to correctly position the group
        MinChildOffsetX = minX - PhysicalX;
        MinChildOffsetY = minY - PhysicalY;

        // Update label bounds (positioned at top-left with padding)
        UpdateLabelBounds(minX, minY);
    }

    /// <summary>
    /// Updates the label bounds based on group name and position.
    /// Label is positioned above the group boundary with appropriate dimensions.
    /// </summary>
    private void UpdateLabelBounds(double groupMinX, double groupMinY)
    {
        const double BorderPadding = 10.0;
        const double LabelHeight = 18.0;

        // Estimate label width based on text (approximate 7 pixels per character + padding)
        double estimatedLabelWidth = Math.Max(60, GroupName.Length * 7 + 8);

        // Position label at top-left of group, above the border
        double labelX = groupMinX - BorderPadding;
        double labelY = groupMinY - BorderPadding - 20;

        LabelBounds = (labelX, labelY, estimatedLabelWidth, LabelHeight);
    }

    /// <summary>
    /// Gets the bounding box for a path segment (including waveguide width padding).
    /// </summary>
    /// <param name="segment">Path segment to calculate bounds for.</param>
    /// <returns>Bounding box as (MinX, MinY, MaxX, MaxY).</returns>
    private (double MinX, double MinY, double MaxX, double MaxY) GetSegmentBounds(PathSegment segment)
    {
        const double WaveguideWidthPadding = 2.0; // Typical waveguide width in micrometers

        if (segment is StraightSegment straight)
        {
            double minX = Math.Min(straight.StartPoint.X, straight.EndPoint.X) - WaveguideWidthPadding;
            double minY = Math.Min(straight.StartPoint.Y, straight.EndPoint.Y) - WaveguideWidthPadding;
            double maxX = Math.Max(straight.StartPoint.X, straight.EndPoint.X) + WaveguideWidthPadding;
            double maxY = Math.Max(straight.StartPoint.Y, straight.EndPoint.Y) + WaveguideWidthPadding;
            return (minX, minY, maxX, maxY);
        }
        else if (segment is BendSegment bend)
        {
            // For arcs, the bounding box depends on which quadrants the arc passes through
            // Conservative approach: use center +/- radius + padding
            double minX = bend.Center.X - bend.RadiusMicrometers - WaveguideWidthPadding;
            double minY = bend.Center.Y - bend.RadiusMicrometers - WaveguideWidthPadding;
            double maxX = bend.Center.X + bend.RadiusMicrometers + WaveguideWidthPadding;
            double maxY = bend.Center.Y + bend.RadiusMicrometers + WaveguideWidthPadding;

            // Refine bounds by checking start and end points
            minX = Math.Min(minX, Math.Min(bend.StartPoint.X, bend.EndPoint.X) - WaveguideWidthPadding);
            minY = Math.Min(minY, Math.Min(bend.StartPoint.Y, bend.EndPoint.Y) - WaveguideWidthPadding);
            maxX = Math.Max(maxX, Math.Max(bend.StartPoint.X, bend.EndPoint.X) + WaveguideWidthPadding);
            maxY = Math.Max(maxY, Math.Max(bend.StartPoint.Y, bend.EndPoint.Y) + WaveguideWidthPadding);

            return (minX, minY, maxX, maxY);
        }

        // Unknown segment type - return zero bounds
        return (0, 0, 0, 0);
    }

    /// <summary>
    /// Gets all components recursively (including nested group children).
    /// </summary>
    public List<Component> GetAllComponentsRecursive()
    {
        var allComponents = new List<Component>();
        foreach (var child in ChildComponents)
        {
            allComponents.Add(child);
            if (child is ComponentGroup childGroup)
            {
                allComponents.AddRange(childGroup.GetAllComponentsRecursive());
            }
        }
        return allComponents;
    }

    /// <summary>
    /// Overrides Component.Clone() to perform a deep copy of the ComponentGroup.
    /// This ensures that copy/paste operations correctly clone child components and internal paths.
    /// </summary>
    /// <returns>A new ComponentGroup instance with cloned children and paths.</returns>
    public override object Clone()
    {
        return DeepCopy();
    }

    /// <summary>
    /// Creates a deep copy of this ComponentGroup with new unique identifiers for all components.
    /// Used for instantiating group templates from the library.
    /// </summary>
    /// <returns>A new ComponentGroup instance with cloned children and paths.</returns>
    public ComponentGroup DeepCopy()
    {
        // Create new group with unique identifier
        var newGroup = new ComponentGroup(GroupName)
        {
            Description = Description,
            Identifier = $"group_{Guid.NewGuid():N}",
            PhysicalX = PhysicalX,
            PhysicalY = PhysicalY,
            WidthMicrometers = WidthMicrometers,
            HeightMicrometers = HeightMicrometers,
            Rotation90CounterClock = Rotation90CounterClock,
            // Immutable records — sharing the list is safe (same rule as Component.DeepCopy).
            OutlinePolygons = OutlinePolygons
        };

        // Map old component Ids (Guid) to new cloned components
        // IMPORTANT: This map must include ALL components recursively, not just direct children,
        // because InternalPaths may reference pins from nested group children
        var componentMap = new Dictionary<Guid, Component>();

        // Deep copy child components
        foreach (var child in ChildComponents)
        {
            Component clonedChild;
            if (child is ComponentGroup childGroup)
            {
                // Recursively clone nested groups
                clonedChild = childGroup.DeepCopy();
            }
            else
            {
                // Clone regular component
                clonedChild = CloneComponent(child);
            }

            // Add the direct child to the map
            componentMap[child.Id] = clonedChild;

            // If the child is a group, also add all its descendants to the map
            // This is needed because InternalPaths may reference pins from nested group children
            if (child is ComponentGroup childGroupForMap && clonedChild is ComponentGroup clonedChildGroup)
            {
                AddDescendantsToMap(childGroupForMap, clonedChildGroup, componentMap);
            }

            newGroup.AddChild(clonedChild);
        }

        // Clone frozen waveguide paths
        foreach (var frozenPath in InternalPaths)
        {
            FrozenWaveguidePath clonedPath;
            if (frozenPath.StartPin is null || frozenPath.EndPin is null)
            {
                // Pin-less geometry (e.g. GDS-imported route outlines): no pins to remap.
                clonedPath = new FrozenWaveguidePath
                {
                    PathId = Guid.NewGuid(),
                    Path = CloneRoutedPath(frozenPath.Path),
                };
            }
            else
            {
                var startComp = componentMap[frozenPath.StartPin.ParentComponent.Id];
                var endComp = componentMap[frozenPath.EndPin.ParentComponent.Id];

                var newStartPin = startComp.PhysicalPins.First(p => p.Name == frozenPath.StartPin.Name);
                var newEndPin = endComp.PhysicalPins.First(p => p.Name == frozenPath.EndPin.Name);

                clonedPath = new FrozenWaveguidePath
                {
                    PathId = Guid.NewGuid(),
                    Path = CloneRoutedPath(frozenPath.Path),
                    StartPin = newStartPin,
                    EndPin = newEndPin
                };
            }
            // Carry the per-connection routing settings into the copy so template
            // instantiation and copy/paste keep the configured styles.
            clonedPath.CopySettingsFrom(frozenPath);

            newGroup.AddInternalPath(clonedPath);
        }

        // Clone external pins
        foreach (var externalPin in ExternalPins)
        {
            var internalComp = componentMap[externalPin.InternalPin.ParentComponent.Id];
            var newInternalPin = internalComp.PhysicalPins.First(p => p.Name == externalPin.InternalPin.Name);

            var clonedPin = new GroupPin
            {
                PinId = Guid.NewGuid(),
                Name = externalPin.Name,
                InternalPin = newInternalPin,
                RelativeX = externalPin.RelativeX,
                RelativeY = externalPin.RelativeY,
                AngleDegrees = externalPin.AngleDegrees
            };

            newGroup.AddExternalPin(clonedPin);
        }

        return newGroup;
    }

    /// <summary>
    /// Recursively adds all descendants of a group to the component map.
    /// This is needed because InternalPaths may reference pins from nested group children.
    /// </summary>
    /// <param name="originalGroup">The original group whose descendants to map.</param>
    /// <param name="clonedGroup">The cloned group whose descendants to add to the map.</param>
    /// <param name="componentMap">The map to populate with original ID -> cloned component.</param>
    private void AddDescendantsToMap(ComponentGroup originalGroup, ComponentGroup clonedGroup, Dictionary<Guid, Component> componentMap)
    {
        // Map each child component by pairing original and cloned children by index
        for (int i = 0; i < originalGroup.ChildComponents.Count; i++)
        {
            var originalChild = originalGroup.ChildComponents[i];
            var clonedChild = clonedGroup.ChildComponents[i];

            // Add this descendant to the map
            componentMap[originalChild.Id] = clonedChild;

            // If the child is a group, recursively add its descendants
            if (originalChild is ComponentGroup originalChildGroup && clonedChild is ComponentGroup clonedChildGroup)
            {
                AddDescendantsToMap(originalChildGroup, clonedChildGroup, componentMap);
            }
        }
    }

    /// <summary>
    /// Creates a shallow clone of a Component with a new unique identifier.
    /// </summary>
    private Component CloneComponent(Component source)
    {
        // Create new component with same S-matrix and structure
        var cloned = new Component(
            new Dictionary<int, SMatrix>(source.WaveLengthToSMatrixMap),
            source.GetAllSliders().Select(s => new Slider(
                Guid.NewGuid(),
                s.Number,
                s.Value,
                s.MaxValue,
                s.MinValue)).ToList(),
            source.NazcaFunctionName,
            source.NazcaFunctionParameters,
            source.Parts,
            source.TypeNumber,
            $"{source.Identifier}_{Guid.NewGuid():N}",
            source.Rotation90CounterClock,
            source.PhysicalPins.Select(p => new PhysicalPin
            {
                Name = p.Name,
                OffsetXMicrometers = p.OffsetXMicrometers,
                OffsetYMicrometers = p.OffsetYMicrometers,
                AngleDegrees = p.AngleDegrees,
                LogicalPin = p.LogicalPin
            }).ToList()
        )
        {
            PhysicalX = source.PhysicalX,
            PhysicalY = source.PhysicalY,
            WidthMicrometers = source.WidthMicrometers,
            HeightMicrometers = source.HeightMicrometers,
            NazcaOriginOffsetX = source.NazcaOriginOffsetX,
            NazcaOriginOffsetY = source.NazcaOriginOffsetY,
            NazcaModuleName = source.NazcaModuleName,
            // gdsfactory-backend marker must survive group copy/paste, else the clone
            // exports as a stub and loses its PDK/process attribution (#570/#661 review).
            GdsFactoryFunction = source.GdsFactoryFunction,
            HumanReadableName = source.HumanReadableName, // Preserve human-readable name
            IsLocked = false // Don't copy lock state
        };

        return cloned;
    }

    /// <summary>
    /// Creates a deep clone of a RoutedPath.
    /// </summary>
    private RoutedPath CloneRoutedPath(RoutedPath source)
    {
        var cloned = new RoutedPath
        {
            IsBlockedFallback = source.IsBlockedFallback,
            IsInvalidGeometry = source.IsInvalidGeometry,
            IsPlaceholderGeometry = source.IsPlaceholderGeometry
        };

        foreach (var segment in source.Segments)
        {
            if (segment is BendSegment bend)
            {
                cloned.Segments.Add(new BendSegment(
                    bend.Center.X,
                    bend.Center.Y,
                    bend.RadiusMicrometers,
                    bend.StartAngleDegrees,
                    bend.SweepAngleDegrees
                ));
            }
            else if (segment is StraightSegment straight)
            {
                cloned.Segments.Add(new StraightSegment(
                    straight.StartPoint.X,
                    straight.StartPoint.Y,
                    straight.EndPoint.X,
                    straight.EndPoint.Y,
                    straight.StartAngleDegrees
                ));
            }
        }

        return cloned;
    }

    /// <summary>
    /// Raises the PropertyChanged event.
    /// </summary>
    /// <param name="propertyName">Name of the property that changed.</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Updates label bounds when the group name changes (affects label width).
    /// </summary>
    private void UpdateLabelBoundsAfterNameChange()
    {
        // Recalculate label bounds based on new name length
        if (ChildComponents.Count > 0 || InternalPaths.Count > 0)
        {
            UpdateGroupBounds();
        }
    }

    /// <summary>
    /// Invalidates the cached S-Matrix, forcing recomputation on next access.
    /// Called when group structure changes (children, paths, or external pins modified).
    /// </summary>
    private void InvalidateSMatrix()
    {
        // Clear the cached S-Matrix dictionary
        WaveLengthToSMatrixMap.Clear();
    }

    /// <summary>
    /// Computes or retrieves the S-Matrix for this group at all supported wavelengths.
    /// The S-Matrix is cached until the group structure changes.
    /// </summary>
    public void ComputeSMatrix()
    {
        // Early exit if group has no external pins (can't participate in simulation)
        if (ExternalPins.Count == 0)
            return;

        // Lazy-initialize the builder
        _sMatrixBuilder ??= new ComponentGroupSMatrixBuilder();

        // Build S-Matrices for all wavelengths
        var matrices = _sMatrixBuilder.BuildGroupSMatrixAllWavelengths(this);

        if (matrices != null)
        {
            // Update the component's S-Matrix dictionary
            WaveLengthToSMatrixMap = matrices;

            // Populate PhysicalPins from ExternalPins for simulation connectivity
            SyncPhysicalPinsFromExternalPins();
        }
    }

    /// <summary>
    /// Synchronizes the PhysicalPins collection with ExternalPins.
    /// This allows the simulation framework to connect to the group's external pins.
    /// </summary>
    private void SyncPhysicalPinsFromExternalPins()
    {
        PhysicalPins.Clear();

        foreach (var externalPin in ExternalPins)
        {
            var physicalPin = new PhysicalPin
            {
                Name = externalPin.Name,
                ParentComponent = this,
                OffsetXMicrometers = externalPin.RelativeX,
                OffsetYMicrometers = externalPin.RelativeY,
                AngleDegrees = externalPin.AngleDegrees,
                LogicalPin = externalPin.InternalPin.LogicalPin
            };

            PhysicalPins.Add(physicalPin);
        }
    }

    /// <summary>
    /// Ensures the S-Matrix is computed and up-to-date.
    /// Call this before using the group in simulation.
    /// </summary>
    public void EnsureSMatrixComputed()
    {
        if (WaveLengthToSMatrixMap.Count == 0 && ExternalPins.Count > 0)
        {
            ComputeSMatrix();
        }
    }
}
