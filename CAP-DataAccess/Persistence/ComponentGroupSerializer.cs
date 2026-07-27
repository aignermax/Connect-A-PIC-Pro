using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_DataAccess.Persistence.DTOs;

namespace CAP_DataAccess.Persistence;

/// <summary>
/// Serializes and deserializes ComponentGroup instances to/from DTOs.
/// Handles hierarchical structure, frozen paths, and external pin mappings.
/// </summary>
public static class ComponentGroupSerializer
{
    /// <summary>
    /// Converts a ComponentGroup to a DTO for JSON serialization.
    /// </summary>
    /// <param name="group">The ComponentGroup to serialize.</param>
    /// <returns>DTO representation of the group.</returns>
    public static ComponentGroupDto ToDto(ComponentGroup group)
    {
        if (group == null)
            throw new ArgumentNullException(nameof(group));

        var dto = new ComponentGroupDto
        {
            GroupName = group.GroupName,
            Description = group.Description,
            Identifier = group.Identifier,
            IdGuid = group.Id.ToString(),
            GridX = group.GridXMainTile,
            GridY = group.GridYMainTile,
            PhysicalX = group.PhysicalX,
            PhysicalY = group.PhysicalY,
            Rotation90CounterClock = (int)group.Rotation90CounterClock,
            ParentGroupId = group.ParentGroup?.Identifier,
            ParentGroupIdGuid = group.ParentGroup?.Id.ToString()
        };

        // Add child component IDs (both name and Guid for forward/backward compat)
        foreach (var child in group.ChildComponents)
        {
            dto.ChildComponentIds.Add(child.Identifier);
            dto.ChildComponentGuids.Add(child.Id.ToString());
        }

        // Serialize frozen paths
        foreach (var frozenPath in group.InternalPaths)
        {
            dto.InternalPaths.Add(ToFrozenPathDto(frozenPath));
        }

        // Serialize external pins
        foreach (var pin in group.ExternalPins)
        {
            dto.ExternalPins.Add(ToGroupPinDto(pin));
        }

        return dto;
    }

    /// <summary>
    /// Reconstructs a ComponentGroup from a DTO after all individual components are loaded.
    /// Uses name-based lookup (backward-compatible path for old files and grid persistence).
    /// </summary>
    /// <param name="dto">The DTO containing group data.</param>
    /// <param name="componentLookup">Dictionary mapping component names to Component instances.</param>
    /// <returns>Reconstructed ComponentGroup.</returns>
    public static ComponentGroup FromDto(
        ComponentGroupDto dto,
        Dictionary<string, Component> componentLookup)
    {
        if (dto == null)
            throw new ArgumentNullException(nameof(dto));
        if (componentLookup == null)
            throw new ArgumentNullException(nameof(componentLookup));

        return FromDtoCore(dto, guidLookup: null, nameLookup: componentLookup);
    }

    /// <summary>
    /// Reconstructs a ComponentGroup from a DTO using Guid-based lookup.
    /// Falls back to name-based lookup for old files that predate Guid fields.
    /// </summary>
    /// <param name="dto">The DTO containing group data.</param>
    /// <param name="guidLookup">Dictionary mapping saved component Guids to Component instances.</param>
    /// <param name="nameFallback">Optional name-based fallback for old files.</param>
    /// <returns>Reconstructed ComponentGroup.</returns>
    public static ComponentGroup FromDto(
        ComponentGroupDto dto,
        Dictionary<Guid, Component> guidLookup,
        Dictionary<string, Component>? nameFallback = null)
    {
        if (dto == null)
            throw new ArgumentNullException(nameof(dto));
        if (guidLookup == null)
            throw new ArgumentNullException(nameof(guidLookup));

        return FromDtoCore(dto, guidLookup, nameFallback);
    }

    /// <summary>
    /// Core reconstruction logic shared by both FromDto overloads.
    /// </summary>
    private static ComponentGroup FromDtoCore(
        ComponentGroupDto dto,
        Dictionary<Guid, Component>? guidLookup,
        Dictionary<string, Component>? nameLookup)
    {
        // Create the group
        var group = new ComponentGroup(dto.GroupName)
        {
            Description = dto.Description,
            Identifier = dto.Identifier,
            PhysicalX = dto.PhysicalX,
            PhysicalY = dto.PhysicalY,
            Rotation90CounterClock = (DiscreteRotation)dto.Rotation90CounterClock
        };

        // Add child components - prefer Guid lookup, fall back to name lookup
        var useGuids = guidLookup != null && dto.ChildComponentGuids.Count == dto.ChildComponentIds.Count
                       && dto.ChildComponentGuids.Count > 0;

        for (int i = 0; i < dto.ChildComponentIds.Count; i++)
        {
            var childId = dto.ChildComponentIds[i];
            Component? childComponent = null;

            if (useGuids && Guid.TryParse(dto.ChildComponentGuids[i], out var childGuid))
            {
                guidLookup!.TryGetValue(childGuid, out childComponent);
            }

            if (childComponent == null && nameLookup != null)
            {
                nameLookup.TryGetValue(childId, out childComponent);
            }

            if (childComponent == null)
            {
                throw new InvalidOperationException(
                    $"Child component '{childId}' not found in component lookup.");
            }

            group.AddChild(childComponent);
        }

        // Reconstruct frozen paths
        foreach (var pathDto in dto.InternalPaths)
        {
            var frozenPath = FromFrozenPathDto(pathDto, guidLookup, nameLookup);
            group.AddInternalPath(frozenPath);
        }

        // Reconstruct external pins
        foreach (var pinDto in dto.ExternalPins)
        {
            var groupPin = FromGroupPinDto(pinDto, guidLookup, nameLookup);
            group.AddExternalPin(groupPin);
        }

        return group;
    }

    /// <summary>
    /// Resolves a component from Guid or name lookup with fallback logic.
    /// </summary>
    private static Component ResolveComponent(
        string? guidStr,
        string nameId,
        Dictionary<Guid, Component>? guidLookup,
        Dictionary<string, Component>? nameLookup)
    {
        if (guidStr != null && guidLookup != null && Guid.TryParse(guidStr, out var guid))
        {
            if (guidLookup.TryGetValue(guid, out var byGuid)) return byGuid;
        }

        if (nameLookup != null && nameLookup.TryGetValue(nameId, out var byName)) return byName;

        throw new InvalidOperationException(
            $"Component '{nameId}' (Guid: {guidStr ?? "none"}) not found in lookup.");
    }

    /// <summary>
    /// Converts a FrozenWaveguidePath to a DTO.
    /// </summary>
    private static FrozenPathDto ToFrozenPathDto(FrozenWaveguidePath frozenPath)
    {
        var dto = new FrozenPathDto
        {
            PathId = frozenPath.PathId.ToString(),
            StartComponentId = frozenPath.StartPin.ParentComponent.Identifier,
            StartComponentGuid = frozenPath.StartPin.ParentComponent.Id.ToString(),
            StartPinName = frozenPath.StartPin.Name,
            EndComponentId = frozenPath.EndPin.ParentComponent.Identifier,
            EndComponentGuid = frozenPath.EndPin.ParentComponent.Id.ToString(),
            EndPinName = frozenPath.EndPin.Name,
            IsBlockedFallback = frozenPath.Path.IsBlockedFallback,
            IsInvalidGeometry = frozenPath.Path.IsInvalidGeometry,
            ConnectionType = frozenPath.ConnectionType.ToString(),
            BendRadiusMicrometers = frozenPath.BendRadiusMicrometers,
            WidthMicrometers = frozenPath.WidthMicrometers,
            IsRouteFrozen = frozenPath.IsRouteFrozen,
            PropagationLossDbPerCm = frozenPath.PropagationLossDbPerCm,
            BendRadiusOverrides = new Dictionary<int, double>(frozenPath.BendRadiusOverrides),
            StraightShiftOffsets = new Dictionary<int, double>(frozenPath.StraightShiftOffsets)
        };

        // Serialize path segments
        foreach (var segment in frozenPath.Path.Segments)
        {
            dto.Segments.Add(ToPathSegmentDto(segment));
        }

        return dto;
    }

    /// <summary>
    /// Reconstructs a FrozenWaveguidePath from a DTO (name-based, backward-compat).
    /// </summary>
    private static FrozenWaveguidePath FromFrozenPathDto(
        FrozenPathDto dto,
        Dictionary<string, Component> componentLookup)
        => FromFrozenPathDto(dto, null, componentLookup);

    /// <summary>
    /// Reconstructs a FrozenWaveguidePath from a DTO with dual-lookup support.
    /// </summary>
    private static FrozenWaveguidePath FromFrozenPathDto(
        FrozenPathDto dto,
        Dictionary<Guid, Component>? guidLookup,
        Dictionary<string, Component>? nameLookup)
    {
        var startComp = ResolveComponent(dto.StartComponentGuid, dto.StartComponentId, guidLookup, nameLookup);
        var endComp = ResolveComponent(dto.EndComponentGuid, dto.EndComponentId, guidLookup, nameLookup);

        var startPin = startComp.PhysicalPins.FirstOrDefault(p => p.Name == dto.StartPinName);
        var endPin = endComp.PhysicalPins.FirstOrDefault(p => p.Name == dto.EndPinName);

        if (startPin == null)
        {
            throw new InvalidOperationException(
                $"Start pin '{dto.StartPinName}' not found on component '{dto.StartComponentId}'.");
        }

        if (endPin == null)
        {
            throw new InvalidOperationException(
                $"End pin '{dto.EndPinName}' not found on component '{dto.EndComponentId}'.");
        }

        // Reconstruct path
        var path = new RoutedPath
        {
            IsBlockedFallback = dto.IsBlockedFallback,
            IsInvalidGeometry = dto.IsInvalidGeometry
        };

        foreach (var segmentDto in dto.Segments)
        {
            path.Segments.Add(FromPathSegmentDto(segmentDto));
        }

        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.Parse(dto.PathId),
            Path = path,
            StartPin = startPin,
            EndPin = endPin
        };
        ApplyConnectionSettings(dto, frozenPath);
        return frozenPath;
    }

    /// <summary>
    /// Restores the per-connection routing settings from the DTO. Design files written
    /// before these fields existed leave them null/absent and keep the model defaults
    /// ("Auto" style); unknown style names also fall back to Auto.
    /// </summary>
    private static void ApplyConnectionSettings(FrozenPathDto dto, FrozenWaveguidePath frozenPath)
    {
        if (Enum.TryParse<WaveguideType>(dto.ConnectionType, out var connectionType)
            && Enum.IsDefined(connectionType))
        {
            frozenPath.ConnectionType = connectionType;
        }
        if (dto.BendRadiusMicrometers is double bendRadius)
            frozenPath.BendRadiusMicrometers = bendRadius;
        if (dto.WidthMicrometers is double width)
            frozenPath.WidthMicrometers = width;
        frozenPath.IsRouteFrozen = dto.IsRouteFrozen;
        if (dto.PropagationLossDbPerCm is double loss)
            frozenPath.PropagationLossDbPerCm = loss;
        if (dto.BendRadiusOverrides != null)
        {
            foreach (var (bendIndex, radius) in dto.BendRadiusOverrides)
                frozenPath.BendRadiusOverrides[bendIndex] = radius;
        }
        if (dto.StraightShiftOffsets != null)
        {
            foreach (var (straightIndex, offset) in dto.StraightShiftOffsets)
                frozenPath.StraightShiftOffsets[straightIndex] = offset;
        }
    }

    /// <summary>
    /// Converts a PathSegment to a DTO.
    /// </summary>
    private static PathSegmentDto ToPathSegmentDto(PathSegment segment)
    {
        var dto = new PathSegmentDto
        {
            StartX = segment.StartPoint.X,
            StartY = segment.StartPoint.Y,
            EndX = segment.EndPoint.X,
            EndY = segment.EndPoint.Y,
            StartAngleDegrees = segment.StartAngleDegrees,
            EndAngleDegrees = segment.EndAngleDegrees
        };

        if (segment is BendSegment bend)
        {
            dto.Type = "arc";
            dto.CenterX = bend.Center.X;
            dto.CenterY = bend.Center.Y;
            dto.RadiusMicrometers = bend.RadiusMicrometers;
            dto.SweepAngleDegrees = bend.SweepAngleDegrees;
        }
        else
        {
            dto.Type = "straight";
        }

        return dto;
    }

    /// <summary>
    /// Reconstructs a PathSegment from a DTO.
    /// </summary>
    private static PathSegment FromPathSegmentDto(PathSegmentDto dto)
    {
        if (dto.Type == "arc")
        {
            return new BendSegment(
                dto.CenterX ?? 0,
                dto.CenterY ?? 0,
                dto.RadiusMicrometers ?? 0,
                dto.StartAngleDegrees,
                dto.SweepAngleDegrees ?? 0
            );
        }

        return new StraightSegment(
            dto.StartX,
            dto.StartY,
            dto.EndX,
            dto.EndY,
            dto.StartAngleDegrees
        );
    }

    /// <summary>
    /// Converts a GroupPin to a DTO.
    /// </summary>
    private static GroupPinDto ToGroupPinDto(GroupPin pin)
    {
        return new GroupPinDto
        {
            PinId = pin.PinId.ToString(),
            Name = pin.Name,
            InternalComponentId = pin.InternalPin.ParentComponent.Identifier,
            InternalComponentGuid = pin.InternalPin.ParentComponent.Id.ToString(),
            InternalPinName = pin.InternalPin.Name,
            RelativeX = pin.RelativeX,
            RelativeY = pin.RelativeY,
            AngleDegrees = pin.AngleDegrees
        };
    }

    /// <summary>
    /// Reconstructs a GroupPin from a DTO (name-based, backward-compat).
    /// </summary>
    private static GroupPin FromGroupPinDto(
        GroupPinDto dto,
        Dictionary<string, Component> componentLookup)
        => FromGroupPinDto(dto, null, componentLookup);

    /// <summary>
    /// Reconstructs a GroupPin from a DTO with dual-lookup support.
    /// </summary>
    private static GroupPin FromGroupPinDto(
        GroupPinDto dto,
        Dictionary<Guid, Component>? guidLookup,
        Dictionary<string, Component>? nameLookup)
    {
        var internalComp = ResolveComponent(
            dto.InternalComponentGuid, dto.InternalComponentId, guidLookup, nameLookup);

        var internalPin = internalComp.PhysicalPins.FirstOrDefault(p => p.Name == dto.InternalPinName);
        if (internalPin == null)
        {
            throw new InvalidOperationException(
                $"Internal pin '{dto.InternalPinName}' not found on component '{dto.InternalComponentId}'.");
        }

        return new GroupPin
        {
            PinId = Guid.Parse(dto.PinId),
            Name = dto.Name,
            InternalPin = internalPin,
            RelativeX = dto.RelativeX,
            RelativeY = dto.RelativeY,
            AngleDegrees = dto.AngleDegrees
        };
    }
}
