namespace CAP_DataAccess.Persistence.DTOs;

/// <summary>
/// Data Transfer Object for ComponentGroup JSON serialization.
/// Stores all data needed to reconstruct a ComponentGroup with its hierarchy,
/// frozen paths, and external pin mappings.
/// </summary>
public class ComponentGroupDto
{
    /// <summary>
    /// Human-readable name for this group.
    /// </summary>
    public string GroupName { get; set; } = "";

    /// <summary>
    /// Optional description of this group's purpose.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Component identifier (human-readable name, for backward compat and display).
    /// </summary>
    public string Identifier { get; set; } = "";

    /// <summary>
    /// Stable unique ID (Guid string) for this group.
    /// Used as the primary lookup key; falls back to Identifier for old files.
    /// </summary>
    public string? IdGuid { get; set; }

    /// <summary>
    /// Stable unique ID (Guid string) of parent group.
    /// Falls back to ParentGroupId for old files.
    /// </summary>
    public string? ParentGroupIdGuid { get; set; }

    /// <summary>
    /// Guid-string IDs of child components, parallel to ChildComponentIds.
    /// Used as the primary lookup key; falls back to ChildComponentIds for old files.
    /// </summary>
    public List<string> ChildComponentGuids { get; set; } = new();

    /// <summary>
    /// Grid X position of main tile.
    /// </summary>
    public int GridX { get; set; }

    /// <summary>
    /// Grid Y position of main tile.
    /// </summary>
    public int GridY { get; set; }

    /// <summary>
    /// Physical X position in micrometers.
    /// </summary>
    public double PhysicalX { get; set; }

    /// <summary>
    /// Physical Y position in micrometers.
    /// </summary>
    public double PhysicalY { get; set; }

    /// <summary>
    /// Rotation in 90-degree counter-clockwise increments (0, 1, 2, or 3).
    /// </summary>
    public int Rotation90CounterClock { get; set; }

    /// <summary>
    /// List of child component identifiers (by Identifier property).
    /// Used to rebuild the parent-child relationships.
    /// </summary>
    public List<string> ChildComponentIds { get; set; } = new();

    /// <summary>
    /// Frozen waveguide paths between child components.
    /// </summary>
    public List<FrozenPathDto> InternalPaths { get; set; } = new();

    /// <summary>
    /// External pins exposed by this group for connections to outside components.
    /// </summary>
    public List<GroupPinDto> ExternalPins { get; set; } = new();

    /// <summary>
    /// Identifier of parent group (null if top-level).
    /// </summary>
    public string? ParentGroupId { get; set; }
}

/// <summary>
/// DTO for frozen waveguide paths within a ComponentGroup.
/// </summary>
public class FrozenPathDto
{
    /// <summary>
    /// Unique identifier for this frozen path.
    /// </summary>
    public string PathId { get; set; } = "";

    /// <summary>
    /// Identifier of the start pin's parent component (human-readable, for old files).
    /// </summary>
    public string StartComponentId { get; set; } = "";

    /// <summary>
    /// Guid string of the start pin's parent component. Primary lookup key.
    /// </summary>
    public string? StartComponentGuid { get; set; }

    /// <summary>
    /// Name of the start pin on the start component.
    /// </summary>
    public string StartPinName { get; set; } = "";

    /// <summary>
    /// Identifier of the end pin's parent component (human-readable, for old files).
    /// </summary>
    public string EndComponentId { get; set; } = "";

    /// <summary>
    /// Guid string of the end pin's parent component. Primary lookup key.
    /// </summary>
    public string? EndComponentGuid { get; set; }

    /// <summary>
    /// Name of the end pin on the end component.
    /// </summary>
    public string EndPinName { get; set; } = "";

    /// <summary>
    /// Path segments (geometry data).
    /// </summary>
    public List<PathSegmentDto> Segments { get; set; } = new();

    /// <summary>
    /// Whether this is a blocked fallback path.
    /// </summary>
    public bool IsBlockedFallback { get; set; }

    /// <summary>
    /// Whether the path has geometry violations.
    /// </summary>
    public bool IsInvalidGeometry { get; set; }

    /// <summary>
    /// Routing style name of the original connection ("Auto", "Bend", "SBend", "Cobra").
    /// Null in design files that predate settings persistence — loads as Auto.
    /// </summary>
    public string? ConnectionType { get; set; }

    /// <summary>
    /// Bend radius of the original connection in micrometers.
    /// Null in old design files — loads with the model default.
    /// </summary>
    public double? BendRadiusMicrometers { get; set; }

    /// <summary>
    /// Waveguide width of the original connection in micrometers.
    /// Null in old design files — loads with the model default.
    /// </summary>
    public double? WidthMicrometers { get; set; }

    /// <summary>
    /// Whether the original connection's route was frozen. Missing in old files — false.
    /// </summary>
    public bool IsRouteFrozen { get; set; }

    /// <summary>
    /// Propagation loss of the original connection in dB/cm.
    /// Null in old design files — loads with the model default.
    /// </summary>
    public double? PropagationLossDbPerCm { get; set; }

    /// <summary>
    /// Manual per-bend radius overrides keyed by bend index.
    /// Null in old design files — loads empty.
    /// </summary>
    public Dictionary<int, double>? BendRadiusOverrides { get; set; }

    /// <summary>
    /// Manual straight-segment shift offsets keyed by straight-segment index (issue #791).
    /// Null in old design files — loads empty.
    /// </summary>
    public Dictionary<int, double>? StraightShiftOffsets { get; set; }
}

/// <summary>
/// DTO for path segments (straight or arc).
/// </summary>
public class PathSegmentDto
{
    /// <summary>
    /// Segment type: "straight" or "arc".
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// Start X position in micrometers.
    /// </summary>
    public double StartX { get; set; }

    /// <summary>
    /// Start Y position in micrometers.
    /// </summary>
    public double StartY { get; set; }

    /// <summary>
    /// End X position in micrometers.
    /// </summary>
    public double EndX { get; set; }

    /// <summary>
    /// End Y position in micrometers.
    /// </summary>
    public double EndY { get; set; }

    /// <summary>
    /// Start angle in degrees.
    /// </summary>
    public double StartAngleDegrees { get; set; }

    /// <summary>
    /// End angle in degrees.
    /// </summary>
    public double EndAngleDegrees { get; set; }

    /// <summary>
    /// Arc center X (only for arc segments).
    /// </summary>
    public double? CenterX { get; set; }

    /// <summary>
    /// Arc center Y (only for arc segments).
    /// </summary>
    public double? CenterY { get; set; }

    /// <summary>
    /// Arc radius in micrometers (only for arc segments).
    /// </summary>
    public double? RadiusMicrometers { get; set; }

    /// <summary>
    /// Arc sweep angle in degrees (only for arc segments).
    /// </summary>
    public double? SweepAngleDegrees { get; set; }
}

/// <summary>
/// DTO for external pins exposed by a ComponentGroup.
/// </summary>
public class GroupPinDto
{
    /// <summary>
    /// Unique identifier for this group pin.
    /// </summary>
    public string PinId { get; set; } = "";

    /// <summary>
    /// External name of this group pin.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Identifier of the internal component that owns this pin (human-readable, for old files).
    /// </summary>
    public string InternalComponentId { get; set; } = "";

    /// <summary>
    /// Guid string of the internal component. Primary lookup key.
    /// </summary>
    public string? InternalComponentGuid { get; set; }

    /// <summary>
    /// Name of the internal pin on the internal component.
    /// </summary>
    public string InternalPinName { get; set; } = "";

    /// <summary>
    /// X position relative to the group's origin (micrometers).
    /// </summary>
    public double RelativeX { get; set; }

    /// <summary>
    /// Y position relative to the group's origin (micrometers).
    /// </summary>
    public double RelativeY { get; set; }

    /// <summary>
    /// Pin angle in degrees (0° = east, 90° = north, etc.).
    /// </summary>
    public double AngleDegrees { get; set; }
}
