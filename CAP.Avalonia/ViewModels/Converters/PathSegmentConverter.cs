using CAP_Core.Routing;

namespace CAP.Avalonia.ViewModels.Converters;

/// <summary>
/// Converts between PathSegment domain objects and PathSegmentData DTOs for serialization.
/// </summary>
public static class PathSegmentConverter
{
    public static PathSegmentData ToDto(PathSegment segment)
    {
        var dto = new PathSegmentData
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
            dto.Type = "Bend";
            dto.CenterX = bend.Center.X;
            dto.CenterY = bend.Center.Y;
            dto.RadiusMicrometers = bend.RadiusMicrometers;
            dto.SweepAngleDegrees = bend.SweepAngleDegrees;
        }
        else
        {
            dto.Type = "Straight";
        }

        return dto;
    }

    public static PathSegment? FromDto(PathSegmentData dto)
    {
        return dto.Type switch
        {
            "Straight" => new StraightSegment(
                dto.StartX, dto.StartY,
                dto.EndX, dto.EndY,
                dto.StartAngleDegrees),

            "Bend" => new BendSegment(
                dto.CenterX ?? 0,
                dto.CenterY ?? 0,
                dto.RadiusMicrometers ?? 10.0,
                dto.StartAngleDegrees,
                dto.SweepAngleDegrees ?? 0),

            _ => null
        };
    }

    public static List<PathSegmentData> ToDtoList(IEnumerable<PathSegment> segments)
    {
        return segments.Select(ToDto).ToList();
    }

    /// <summary>
    /// Reconstructs a RoutedPath from serialized segment DTOs.
    /// Returns null if input is null, empty, or contains only unknown segment types.
    /// </summary>
    /// <param name="segmentDtos">Serialized segment geometry.</param>
    /// <param name="isBlockedFallback">Whether the cached route was flagged blocked.</param>
    /// <param name="isInvalidGeometry">
    /// Whether the cached route violates a physical constraint (bend radius); false for
    /// files saved before this field existed, matching their pre-existing behavior.
    /// </param>
    /// <param name="isPlaceholderGeometry">
    /// Whether the cached route is an honest placeholder rather than real geometry. Null
    /// means the file predates this field (never written as an explicit true/false) — the
    /// flag is then inferred from the route's shape via
    /// <see cref="RoutedPathLegacyMigration.InferPlaceholderGeometry"/> rather than trusted
    /// as false, so a self-crossing placeholder saved before the field existed does not
    /// silently re-export.
    /// </param>
    public static RoutedPath? ToRoutedPath(
        List<PathSegmentData>? segmentDtos, bool isBlockedFallback,
        bool isInvalidGeometry = false, bool? isPlaceholderGeometry = null)
    {
        if (segmentDtos == null || segmentDtos.Count == 0)
            return null;

        var routedPath = new RoutedPath
        {
            IsBlockedFallback = isBlockedFallback,
            IsInvalidGeometry = isInvalidGeometry,
        };

        foreach (var dto in segmentDtos)
        {
            var segment = FromDto(dto);
            if (segment != null)
            {
                routedPath.Segments.Add(segment);
            }
        }

        if (routedPath.Segments.Count == 0)
            return null;

        routedPath.IsPlaceholderGeometry = isPlaceholderGeometry ??
            RoutedPathLegacyMigration.InferPlaceholderGeometry(isBlockedFallback, routedPath.Segments);

        return routedPath;
    }
}
