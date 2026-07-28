using System.Text.Json;
using System.Text.Json.Serialization;

namespace CAP_Core.Routing;

/// <summary>
/// Serializes routed paths to JSON for external visualization tools.
/// Outputs segment geometry (arcs and straight lines) in a simple format.
/// </summary>
public static class RoutedPathSerializer
{
    /// <summary>
    /// Converts a routed path to a JSON string for visualization.
    /// </summary>
    /// <param name="path">The routed path</param>
    /// <returns>JSON representation of the path segments</returns>
    public static string ToJson(RoutedPath path)
    {
        var dto = ToDto(path);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>
    /// Converts a routed path to a serializable DTO.
    /// </summary>
    /// <param name="path">The routed path</param>
    /// <returns>DTO representation</returns>
    public static RoutedPathDto ToDto(RoutedPath path)
    {
        var dto = new RoutedPathDto
        {
            IsValid = path.IsValid,
            IsBlockedFallback = path.IsBlockedFallback,
            IsInvalidGeometry = path.IsInvalidGeometry,
            IsPlaceholderGeometry = path.IsPlaceholderGeometry,
            TotalLengthMicrometers = path.TotalLengthMicrometers,
            TotalEquivalent90DegreeBends = path.TotalEquivalent90DegreeBends,
        };

        foreach (var segment in path.Segments)
        {
            dto.Segments.Add(ToSegmentDto(segment));
        }

        return dto;
    }

    /// <summary>
    /// Serializes multiple routed paths (e.g., all connections) to JSON.
    /// </summary>
    /// <param name="paths">Dictionary of connection ID to routed path</param>
    /// <returns>JSON string</returns>
    public static string ToJson(Dictionary<string, RoutedPath> paths)
    {
        var dtos = paths.ToDictionary(
            kvp => kvp.Key,
            kvp => ToDto(kvp.Value));
        return JsonSerializer.Serialize(dtos, JsonOptions);
    }

    private static SegmentDto ToSegmentDto(PathSegment segment)
    {
        if (segment is BendSegment bend)
        {
            return new SegmentDto
            {
                Type = "arc",
                StartX = bend.StartPoint.X,
                StartY = bend.StartPoint.Y,
                EndX = bend.EndPoint.X,
                EndY = bend.EndPoint.Y,
                CenterX = bend.Center.X,
                CenterY = bend.Center.Y,
                RadiusMicrometers = bend.RadiusMicrometers,
                SweepAngleDegrees = bend.SweepAngleDegrees,
                StartAngleDegrees = bend.StartAngleDegrees,
                EndAngleDegrees = bend.EndAngleDegrees,
            };
        }

        return new SegmentDto
        {
            Type = "straight",
            StartX = segment.StartPoint.X,
            StartY = segment.StartPoint.Y,
            EndX = segment.EndPoint.X,
            EndY = segment.EndPoint.Y,
            StartAngleDegrees = segment.StartAngleDegrees,
            EndAngleDegrees = segment.EndAngleDegrees,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// DTO for serializing a complete routed path.
/// </summary>
public class RoutedPathDto
{
    /// <summary>
    /// Whether the path segments connect properly.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Whether this is a blocked fallback path.
    /// </summary>
    public bool IsBlockedFallback { get; set; }

    /// <summary>
    /// Whether the path has geometry violations.
    /// </summary>
    public bool IsInvalidGeometry { get; set; }

    /// <summary>
    /// Whether the path is an honest placeholder rather than real geometry.
    /// </summary>
    public bool IsPlaceholderGeometry { get; set; }

    /// <summary>
    /// Total path length in micrometers.
    /// </summary>
    public double TotalLengthMicrometers { get; set; }

    /// <summary>
    /// Total equivalent 90-degree bends.
    /// </summary>
    public double TotalEquivalent90DegreeBends { get; set; }

    /// <summary>
    /// Path segments.
    /// </summary>
    public List<SegmentDto> Segments { get; set; } = new();
}

/// <summary>
/// DTO for serializing a single path segment.
/// </summary>
public class SegmentDto
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
