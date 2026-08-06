using System.Text.Json;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;

namespace CAP_Core.Components.Creation;

/// <summary>
/// Self-contained serializer for ComponentGroup templates.
/// Unlike ComponentGroupSerializer in CAP-DataAccess which uses external component references,
/// this serializer embeds all child component data inline for independent storage and retrieval.
/// </summary>
public static class GroupTemplateSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Serializes a ComponentGroup to a self-contained JSON string.
    /// All child components, frozen paths, and external pins are embedded inline.
    /// </summary>
    public static string Serialize(ComponentGroup group)
    {
        if (group == null)
            throw new ArgumentNullException(nameof(group));

        var dto = ToDto(group);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>
    /// Deserializes a ComponentGroup from a self-contained JSON string.
    /// </summary>
    public static ComponentGroup? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var dto = JsonSerializer.Deserialize<GroupTemplateDto>(json, JsonOptions);
        return dto == null ? null : FromDto(dto);
    }

    /// <summary>
    /// Converts a ComponentGroup to a self-contained DTO.
    /// </summary>
    private static GroupTemplateDto ToDto(ComponentGroup group)
    {
        var dto = new GroupTemplateDto
        {
            GroupName = group.GroupName,
            Description = group.Description,
            Identifier = group.Identifier,
            PhysicalX = group.PhysicalX,
            PhysicalY = group.PhysicalY,
            WidthMicrometers = group.WidthMicrometers,
            HeightMicrometers = group.HeightMicrometers,
            Rotation = (int)group.Rotation90CounterClock
        };

        // Serialize child components inline
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                dto.Children.Add(new ChildComponentDto
                {
                    IsGroup = true,
                    NestedGroup = ToDto(childGroup)
                });
            }
            else
            {
                dto.Children.Add(SerializeComponent(child));
            }
        }

        // Serialize frozen paths (reference children by index)
        foreach (var path in group.InternalPaths)
        {
            dto.InternalPaths.Add(SerializeFrozenPath(path, group.ChildComponents));
        }

        // Serialize external pins (reference children by index)
        foreach (var pin in group.ExternalPins)
        {
            dto.ExternalPins.Add(SerializeGroupPin(pin, group.ChildComponents));
        }

        return dto;
    }

    /// <summary>
    /// Reconstructs a ComponentGroup from a self-contained DTO.
    /// </summary>
    private static ComponentGroup FromDto(GroupTemplateDto dto)
    {
        var group = new ComponentGroup(dto.GroupName)
        {
            Description = dto.Description,
            Identifier = dto.Identifier,
            PhysicalX = dto.PhysicalX,
            PhysicalY = dto.PhysicalY,
            WidthMicrometers = dto.WidthMicrometers,
            HeightMicrometers = dto.HeightMicrometers
        };

        // Deserialize child components
        foreach (var childDto in dto.Children)
        {
            Component child;
            if (childDto.IsGroup && childDto.NestedGroup != null)
            {
                child = FromDto(childDto.NestedGroup);
            }
            else
            {
                child = DeserializeComponent(childDto);
            }
            group.AddChild(child);
        }

        // Deserialize frozen paths
        foreach (var pathDto in dto.InternalPaths)
        {
            var frozenPath = DeserializeFrozenPath(pathDto, group.ChildComponents);
            if (frozenPath != null)
            {
                group.AddInternalPath(frozenPath);
            }
        }

        // Deserialize external pins
        foreach (var pinDto in dto.ExternalPins)
        {
            var groupPin = DeserializeGroupPin(pinDto, group.ChildComponents);
            if (groupPin != null)
            {
                group.AddExternalPin(groupPin);
            }
        }

        return group;
    }

    /// <summary>
    /// Serializes a regular Component to a DTO with all data inline,
    /// including logical pin IDs and S-Matrix data so simulation survives a round-trip.
    /// </summary>
    private static ChildComponentDto SerializeComponent(Component comp)
    {
        var pins = comp.PhysicalPins.Select(p => new PinDto
        {
            Name = p.Name,
            OffsetX = p.OffsetXMicrometers,
            OffsetY = p.OffsetYMicrometers,
            AngleDegrees = p.AngleDegrees,
            LogicalPinIdInFlow = p.LogicalPin?.IDInFlow ?? Guid.Empty,
            LogicalPinIdOutFlow = p.LogicalPin?.IDOutFlow ?? Guid.Empty,
            MatterType = p.MatterType,
            Polarization = p.LogicalPin?.Polarization.ToString()
        }).ToList();

        // Serialize S-Matrices so child components keep their simulation data after reload.
        // Without this, deserialized children have empty WaveLengthToSMatrixMap → crash.
        var sMatrices = comp.WaveLengthToSMatrixMap.Select(kvp =>
        {
            var allPinIds = kvp.Value.PinReference.Keys.ToList();
            var transfers = kvp.Value.GetNonNullValues()
                .Select(t => new TransferEntryDto
                {
                    FromPinId = t.Key.PinIdStart,
                    ToPinId = t.Key.PinIdEnd,
                    Real = t.Value.Real,
                    Imaginary = t.Value.Imaginary
                })
                .ToList();

            return new SMatrixEntryDto
            {
                WavelengthNm = kvp.Key,
                AllPinIds = allPinIds,
                Transfers = transfers
            };
        }).ToList();

        return new ChildComponentDto
        {
            IsGroup = false,
            Identifier = comp.Identifier,
            HumanReadableName = comp.HumanReadableName,
            NazcaFunctionName = comp.NazcaFunctionName,
            NazcaFunctionParameters = comp.NazcaFunctionParameters,
            NazcaModuleName = comp.NazcaModuleName,
            GdsFactoryFunction = comp.GdsFactoryFunction,
            TypeNumber = comp.TypeNumber,
            PhysicalX = comp.PhysicalX,
            PhysicalY = comp.PhysicalY,
            WidthMicrometers = comp.WidthMicrometers,
            HeightMicrometers = comp.HeightMicrometers,
            Rotation = (int)comp.Rotation90CounterClock,
            Pins = pins,
            SMatrices = sMatrices
        };
    }

    /// <summary>
    /// Deserializes a regular Component from a DTO, restoring logical pin references
    /// and S-Matrices so the component can participate in simulation after reload.
    /// </summary>
    private static Component DeserializeComponent(ChildComponentDto dto)
    {
        // Reconstruct logical pins with the original Guids so S-Matrix pin references remain valid.
        var physicalPins = dto.Pins.Select(p =>
        {
            Pin? logicalPin = null;
            if (p.LogicalPinIdInFlow != Guid.Empty)
            {
                PolarizationRules.TryParse(p.Polarization, out var polarization);
                logicalPin = new Pin(p.Name, 0, p.MatterType, RectSide.Left,
                    p.LogicalPinIdInFlow, p.LogicalPinIdOutFlow)
                {
                    Polarization = polarization
                };
            }

            return new PhysicalPin
            {
                Name = p.Name,
                OffsetXMicrometers = p.OffsetX,
                OffsetYMicrometers = p.OffsetY,
                AngleDegrees = p.AngleDegrees,
                LogicalPin = logicalPin
            };
        }).ToList();

        // Rebuild S-Matrices from serialized data so children are simulation-ready.
        var sMatrixMap = new Dictionary<int, SMatrix>();
        foreach (var entry in dto.SMatrices)
        {
            var sMatrix = new SMatrix(entry.AllPinIds, new());
            var transfers = entry.Transfers.ToDictionary(
                t => (t.FromPinId, t.ToPinId),
                t => new System.Numerics.Complex(t.Real, t.Imaginary));
            sMatrix.SetValues(transfers);
            sMatrixMap[entry.WavelengthNm] = sMatrix;
        }

        return new Component(
            sMatrixMap,
            new List<Slider>(),
            dto.NazcaFunctionName ?? "",
            dto.NazcaFunctionParameters ?? "",
            new Part[1, 1] { { new Part() } },
            dto.TypeNumber,
            dto.Identifier ?? $"comp_{Guid.NewGuid():N}",
            (DiscreteRotation)dto.Rotation,
            physicalPins)
        {
            PhysicalX = dto.PhysicalX,
            PhysicalY = dto.PhysicalY,
            WidthMicrometers = dto.WidthMicrometers,
            HeightMicrometers = dto.HeightMicrometers,
            NazcaModuleName = dto.NazcaModuleName,
            GdsFactoryFunction = dto.GdsFactoryFunction,
            HumanReadableName = dto.HumanReadableName
        };
    }

    /// <summary>
    /// Serializes a frozen path, referencing child components by index.
    /// Pin-less paths (GDS-imported route outlines) serialize with −1 child
    /// indexes and empty pin names.
    /// </summary>
    private static FrozenPathDto SerializeFrozenPath(
        FrozenWaveguidePath path,
        List<Component> children)
    {
        int startIdx = path.StartPin is null ? -1 : children.IndexOf(path.StartPin.ParentComponent);
        int endIdx = path.EndPin is null ? -1 : children.IndexOf(path.EndPin.ParentComponent);

        var segments = path.Path.Segments.Select(seg =>
        {
            if (seg is BendSegment bend)
            {
                return new SegmentDto
                {
                    Type = "arc",
                    StartX = bend.StartPoint.X,
                    StartY = bend.StartPoint.Y,
                    EndX = bend.EndPoint.X,
                    EndY = bend.EndPoint.Y,
                    StartAngleDegrees = bend.StartAngleDegrees,
                    CenterX = bend.Center.X,
                    CenterY = bend.Center.Y,
                    RadiusMicrometers = bend.RadiusMicrometers,
                    SweepAngleDegrees = bend.SweepAngleDegrees
                };
            }

            return new SegmentDto
            {
                Type = "straight",
                StartX = seg.StartPoint.X,
                StartY = seg.StartPoint.Y,
                EndX = seg.EndPoint.X,
                EndY = seg.EndPoint.Y,
                StartAngleDegrees = seg.StartAngleDegrees
            };
        }).ToList();

        return new FrozenPathDto
        {
            StartChildIndex = startIdx,
            StartPinName = path.StartPin?.Name ?? "",
            EndChildIndex = endIdx,
            EndPinName = path.EndPin?.Name ?? "",
            IsBlockedFallback = path.Path.IsBlockedFallback,
            IsInvalidGeometry = path.Path.IsInvalidGeometry,
            IsPlaceholderGeometry = path.Path.IsPlaceholderGeometry,
            Segments = segments,
            ConnectionType = path.ConnectionType.ToString(),
            BendRadiusMicrometers = path.BendRadiusMicrometers,
            WidthMicrometers = path.WidthMicrometers,
            IsRouteFrozen = path.IsRouteFrozen,
            PropagationLossDbPerCm = path.PropagationLossDbPerCm,
            BendRadiusOverrides = new Dictionary<int, double>(path.BendRadiusOverrides),
            StraightShiftOffsets = new Dictionary<int, double>(path.StraightShiftOffsets)
        };
    }

    /// <summary>
    /// Deserializes a frozen path, resolving child references by index. A path with
    /// BOTH child indexes at −1 is pin-less geometry (GDS-imported route outline) —
    /// it is restored without pins instead of being dropped.
    /// </summary>
    private static FrozenWaveguidePath? DeserializeFrozenPath(
        FrozenPathDto dto,
        List<Component> children)
    {
        PhysicalPin? startPin = null;
        PhysicalPin? endPin = null;
        bool pinLess = dto.StartChildIndex < 0 && dto.EndChildIndex < 0;
        if (!pinLess)
        {
            if (dto.StartChildIndex < 0 || dto.StartChildIndex >= children.Count)
                return null;
            if (dto.EndChildIndex < 0 || dto.EndChildIndex >= children.Count)
                return null;

            var startComp = children[dto.StartChildIndex];
            var endComp = children[dto.EndChildIndex];

            startPin = startComp.PhysicalPins.FirstOrDefault(p => p.Name == dto.StartPinName);
            endPin = endComp.PhysicalPins.FirstOrDefault(p => p.Name == dto.EndPinName);

            if (startPin == null || endPin == null)
                return null;
        }

        var routedPath = new RoutedPath
        {
            IsBlockedFallback = dto.IsBlockedFallback,
            IsInvalidGeometry = dto.IsInvalidGeometry,
        };

        foreach (var seg in dto.Segments)
        {
            if (seg.Type == "arc")
            {
                routedPath.Segments.Add(new BendSegment(
                    seg.CenterX, seg.CenterY,
                    seg.RadiusMicrometers,
                    seg.StartAngleDegrees,
                    seg.SweepAngleDegrees));
            }
            else
            {
                routedPath.Segments.Add(new StraightSegment(
                    seg.StartX, seg.StartY,
                    seg.EndX, seg.EndY,
                    seg.StartAngleDegrees));
            }
        }

        // Null means the template predates this field — infer it from the route's shape
        // instead of trusting a bare false (see FrozenPathDto's doc comment).
        routedPath.IsPlaceholderGeometry = dto.IsPlaceholderGeometry ??
            RoutedPathLegacyMigration.InferPlaceholderGeometry(dto.IsBlockedFallback, routedPath.Segments);

        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            Path = routedPath,
            StartPin = startPin,
            EndPin = endPin
        };
        ApplyConnectionSettings(dto, frozenPath);
        return frozenPath;
    }

    /// <summary>
    /// Restores the per-connection routing settings from the DTO. Templates written
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
    /// Serializes a group pin, referencing internal component by child index.
    /// </summary>
    private static ExternalPinDto SerializeGroupPin(
        GroupPin pin,
        List<Component> children)
    {
        int childIdx = children.IndexOf(pin.InternalPin.ParentComponent);

        return new ExternalPinDto
        {
            Name = pin.Name,
            ChildIndex = childIdx,
            InternalPinName = pin.InternalPin.Name,
            RelativeX = pin.RelativeX,
            RelativeY = pin.RelativeY,
            AngleDegrees = pin.AngleDegrees
        };
    }

    /// <summary>
    /// Deserializes a group pin, resolving internal component by child index.
    /// </summary>
    private static GroupPin? DeserializeGroupPin(
        ExternalPinDto dto,
        List<Component> children)
    {
        if (dto.ChildIndex < 0 || dto.ChildIndex >= children.Count)
            return null;

        var internalComp = children[dto.ChildIndex];
        var internalPin = internalComp.PhysicalPins.FirstOrDefault(
            p => p.Name == dto.InternalPinName);

        if (internalPin == null)
            return null;

        return new GroupPin
        {
            PinId = Guid.NewGuid(),
            Name = dto.Name,
            InternalPin = internalPin,
            RelativeX = dto.RelativeX,
            RelativeY = dto.RelativeY,
            AngleDegrees = dto.AngleDegrees
        };
    }
}

#region DTOs for self-contained group template serialization

/// <summary>
/// Self-contained DTO for a ComponentGroup template.
/// All data needed for reconstruction is embedded inline.
/// </summary>
public class GroupTemplateDto
{
    public string GroupName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Identifier { get; set; } = "";
    public double PhysicalX { get; set; }
    public double PhysicalY { get; set; }
    public double WidthMicrometers { get; set; }
    public double HeightMicrometers { get; set; }
    public int Rotation { get; set; }
    public List<ChildComponentDto> Children { get; set; } = new();
    public List<FrozenPathDto> InternalPaths { get; set; } = new();
    public List<ExternalPinDto> ExternalPins { get; set; } = new();
}

/// <summary>
/// DTO for a child component (regular or nested group).
/// </summary>
public class ChildComponentDto
{
    public bool IsGroup { get; set; }
    public string? Identifier { get; set; }

    /// <summary>
    /// Human-readable display name, separate from Identifier.
    /// </summary>
    public string? HumanReadableName { get; set; }

    public string? NazcaFunctionName { get; set; }
    public string? NazcaFunctionParameters { get; set; }
    public string? NazcaModuleName { get; set; }

    /// <summary>
    /// gdsfactory factory name for gdsfactory-backend components (e.g. "cspdk.sin300.mmi1x2").
    /// Persisted so a saved group template keeps its backend across reloads (#570/#661 review).
    /// </summary>
    public string? GdsFactoryFunction { get; set; }

    public int TypeNumber { get; set; }
    public double PhysicalX { get; set; }
    public double PhysicalY { get; set; }
    public double WidthMicrometers { get; set; }
    public double HeightMicrometers { get; set; }
    public int Rotation { get; set; }
    public List<PinDto> Pins { get; set; } = new();
    public GroupTemplateDto? NestedGroup { get; set; }

    /// <summary>
    /// S-Matrix data per wavelength. Populated during serialization so that
    /// deserialized components remain simulation-ready without PDK re-loading.
    /// </summary>
    public List<SMatrixEntryDto> SMatrices { get; set; } = new();
}

/// <summary>
/// DTO for a physical pin on a child component.
/// Logical pin IDs are preserved so S-Matrix pin references survive a round-trip.
/// </summary>
public class PinDto
{
    public string Name { get; set; } = "";
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double AngleDegrees { get; set; }
    public Guid LogicalPinIdInFlow { get; set; }
    public Guid LogicalPinIdOutFlow { get; set; }

    /// <summary>
    /// Signal domain of the pin (optical vs. electrical). Persisted so a saved group template
    /// keeps its electrical pins electrical on reload — without it every pin reverted to optical
    /// and cross-kind connection guards were defeated (#519). Defaults to Light for older
    /// templates that predate the field.
    /// </summary>
    public MatterType MatterType { get; set; } = MatterType.Light;

    /// <summary>
    /// Polarization kind of the logical pin ("TE", "TM", "Both").
    /// Null in old prefab files — deserializes to the TE default.
    /// </summary>
    public string? Polarization { get; set; }
}

/// <summary>
/// DTO for one wavelength's S-Matrix inside a serialized child component.
/// Stores all pin IDs and the non-zero transfer coefficients.
/// </summary>
public class SMatrixEntryDto
{
    public int WavelengthNm { get; set; }

    /// <summary>All pin IDs present in the matrix (needed to reconstruct dimensions).</summary>
    public List<Guid> AllPinIds { get; set; } = new();

    /// <summary>Non-zero transfer entries (inflow→outflow with complex coefficient).</summary>
    public List<TransferEntryDto> Transfers { get; set; } = new();
}

/// <summary>
/// DTO for a single transfer coefficient inside an S-Matrix.
/// </summary>
public class TransferEntryDto
{
    public Guid FromPinId { get; set; }
    public Guid ToPinId { get; set; }
    public double Real { get; set; }
    public double Imaginary { get; set; }
}

/// <summary>
/// DTO for a frozen waveguide path within the group.
/// References child components by index in the Children list.
/// </summary>
public class FrozenPathDto
{
    public int StartChildIndex { get; set; }
    public string StartPinName { get; set; } = "";
    public int EndChildIndex { get; set; }
    public string EndPinName { get; set; } = "";
    public bool IsBlockedFallback { get; set; }
    public bool IsInvalidGeometry { get; set; }

    /// <summary>
    /// Whether the path is an honest placeholder rather than real geometry (the router
    /// replaced a self-crossing fallback with a straight line). Nullable — always written
    /// as an explicit true/false when serialized, so null unambiguously means the template
    /// predates this field; deserialization then infers it from the route's shape instead
    /// of trusting a bare false.
    /// </summary>
    public bool? IsPlaceholderGeometry { get; set; }

    public List<SegmentDto> Segments { get; set; } = new();

    /// <summary>
    /// Routing style name of the original connection ("Auto", "Bend", "SBend", "Cobra").
    /// Null in templates that predate settings persistence — loads as Auto.
    /// </summary>
    public string? ConnectionType { get; set; }

    /// <summary>
    /// Bend radius of the original connection in micrometers.
    /// Null in old templates — loads with the model default.
    /// </summary>
    public double? BendRadiusMicrometers { get; set; }

    /// <summary>
    /// Waveguide width of the original connection in micrometers.
    /// Null in old templates — loads with the model default.
    /// </summary>
    public double? WidthMicrometers { get; set; }

    /// <summary>
    /// Whether the original connection's route was frozen. Missing in old templates — false.
    /// </summary>
    public bool IsRouteFrozen { get; set; }

    /// <summary>
    /// Propagation loss of the original connection in dB/cm.
    /// Null in old templates — loads with the model default.
    /// </summary>
    public double? PropagationLossDbPerCm { get; set; }

    /// <summary>
    /// Manual per-bend radius overrides keyed by bend index.
    /// Null in old templates — loads empty.
    /// </summary>
    public Dictionary<int, double>? BendRadiusOverrides { get; set; }

    /// <summary>
    /// Manual straight-segment shift offsets keyed by straight-segment index (issue #791).
    /// Null in old templates — loads empty.
    /// </summary>
    public Dictionary<int, double>? StraightShiftOffsets { get; set; }
}

/// <summary>
/// DTO for a path segment (straight or arc).
/// </summary>
public class SegmentDto
{
    public string Type { get; set; } = "straight";
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public double StartAngleDegrees { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double RadiusMicrometers { get; set; }
    public double SweepAngleDegrees { get; set; }
}

/// <summary>
/// DTO for an external pin exposed by the group.
/// References internal component by index in the Children list.
/// </summary>
public class ExternalPinDto
{
    public string Name { get; set; } = "";
    public int ChildIndex { get; set; }
    public string InternalPinName { get; set; } = "";
    public double RelativeX { get; set; }
    public double RelativeY { get; set; }
    public double AngleDegrees { get; set; }
}

#endregion
