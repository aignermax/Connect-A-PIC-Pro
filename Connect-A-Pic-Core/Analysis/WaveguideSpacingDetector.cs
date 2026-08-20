using System.Globalization;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;

namespace CAP_Core.Analysis;

/// <summary>
/// Detects waveguide route segments whose edge-to-edge distance is below a
/// process-defined minimum spacing. Uses a uniform spatial grid so large
/// imported designs stay responsive.
/// </summary>
public class WaveguideSpacingDetector
{
    private readonly record struct SegmentInfo(
        Guid PathId,
        string PathLabel,
        WaveguideConnection? Connection,
        PathSegment Segment,
        int SegmentIndex,
        double HalfWidthMicrometers,
        double SpacingMicrometers);

    /// <summary>
    /// Detects all distinct waveguide segment pairs whose edge-to-edge clearance
    /// is smaller than the governing minimum spacing. Without a per-connection
    /// provider that is <paramref name="minWaveguideSpacingMicrometers"/> for every
    /// segment; with one (issue #936) each connection's segments carry the minimum of
    /// their own endpoint PDKs' processes (≤0 = no declared limit) while frozen group
    /// paths keep <paramref name="minWaveguideSpacingMicrometers"/>, and a pair is
    /// governed by the stricter (larger) of its two segments' values — so a
    /// Cornerstone route enforces its foundry gap even against a SiEPIC neighbour
    /// that declares no limit of its own.
    /// </summary>
    /// <param name="connections">Regular waveguide connections to check.</param>
    /// <param name="groups">ComponentGroups whose frozen internal paths are checked.</param>
    /// <param name="minWaveguideSpacingMicrometers">
    /// Minimum required edge-to-edge spacing for frozen group paths and — when no
    /// per-connection provider is given — for every connection.
    /// </param>
    /// <param name="spacingForConnection">
    /// Optional per-connection minimum spacing resolver (issue #936); a return ≤0
    /// means the connection's PDKs declare no limit. Null keeps the uniform
    /// <paramref name="minWaveguideSpacingMicrometers"/> behavior.
    /// </param>
    /// <returns>A list of spacing design issues, empty if none found.</returns>
    public List<DesignIssue> DetectViolations(
        IEnumerable<WaveguideConnection> connections,
        IEnumerable<ComponentGroup> groups,
        double minWaveguideSpacingMicrometers,
        Func<WaveguideConnection, double>? spacingForConnection = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(groups);

        var segments = CollectSegments(connections, groups, minWaveguideSpacingMicrometers, spacingForConnection);
        if (segments.Count < 2)
            return new List<DesignIssue>();

        double maxSpacing = 0.0;
        foreach (var segment in segments)
        {
            if (segment.SpacingMicrometers > maxSpacing)
                maxSpacing = segment.SpacingMicrometers;
        }
        if (maxSpacing <= 0)
            return new List<DesignIssue>();

        var pairs = FindCandidatePairs(segments, maxSpacing);
        var issues = new List<DesignIssue>();

        foreach (var (indexA, indexB) in pairs)
        {
            var issue = CheckPair(segments[indexA], segments[indexB]);
            if (issue is not null)
                issues.Add(issue);
        }

        return issues;
    }

    private static List<SegmentInfo> CollectSegments(
        IEnumerable<WaveguideConnection> connections,
        IEnumerable<ComponentGroup> groups,
        double minWaveguideSpacingMicrometers,
        Func<WaveguideConnection, double>? spacingForConnection)
    {
        var segments = new List<SegmentInfo>();

        foreach (var connection in connections)
        {
            if (connection.RoutedPath?.Segments is not { Count: > 0 } routedSegments)
                continue;

            var label = FormatConnectionLabel(connection);
            double halfWidth = connection.WidthMicrometers / 2.0;
            double spacing = spacingForConnection?.Invoke(connection) ?? minWaveguideSpacingMicrometers;

            for (int i = 0; i < routedSegments.Count; i++)
            {
                segments.Add(new SegmentInfo(
                    connection.Id,
                    label,
                    connection,
                    routedSegments[i],
                    i,
                    halfWidth,
                    spacing));
            }
        }

        foreach (var group in groups)
        {
            foreach (var frozen in group.InternalPaths)
            {
                if (frozen.Path?.Segments is not { Count: > 0 } frozenSegments)
                    continue;

                var label = $"Group '{group.Identifier}' frozen path";
                double halfWidth = frozen.WidthMicrometers / 2.0;

                for (int i = 0; i < frozenSegments.Count; i++)
                {
                    segments.Add(new SegmentInfo(
                        frozen.PathId,
                        label,
                        null,
                        frozenSegments[i],
                        i,
                        halfWidth,
                        minWaveguideSpacingMicrometers));
                }
            }
        }

        return segments;
    }

    private static HashSet<(int IndexA, int IndexB)> FindCandidatePairs(
        List<SegmentInfo> segments,
        double maxSpacing)
    {
        double maxHalfWidth = 0.0;
        foreach (var segment in segments)
        {
            if (segment.HalfWidthMicrometers > maxHalfWidth)
                maxHalfWidth = segment.HalfWidthMicrometers;
        }

        double cellSize = maxSpacing + maxHalfWidth * 2.0;

        var buckets = new Dictionary<(int X, int Y), List<int>>();

        for (int i = 0; i < segments.Count; i++)
        {
            var (minX, minY, maxX, maxY) = WaveguideSpacingGeometry.GetPaddedBounds(
                segments[i].Segment, segments[i].HalfWidthMicrometers, segments[i].SpacingMicrometers);

            int bx0 = (int)Math.Floor(minX / cellSize);
            int bx1 = (int)Math.Floor(maxX / cellSize);
            int by0 = (int)Math.Floor(minY / cellSize);
            int by1 = (int)Math.Floor(maxY / cellSize);

            for (int bx = bx0; bx <= bx1; bx++)
            {
                for (int by = by0; by <= by1; by++)
                {
                    var key = (bx, by);
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        buckets[key] = list;
                    }

                    list.Add(i);
                }
            }
        }

        var pairs = new HashSet<(int, int)>();

        for (int i = 0; i < segments.Count; i++)
        {
            var (minX, minY, maxX, maxY) = WaveguideSpacingGeometry.GetPaddedBounds(
                segments[i].Segment, segments[i].HalfWidthMicrometers, segments[i].SpacingMicrometers);

            int bx0 = (int)Math.Floor(minX / cellSize) - 1;
            int bx1 = (int)Math.Floor(maxX / cellSize) + 1;
            int by0 = (int)Math.Floor(minY / cellSize) - 1;
            int by1 = (int)Math.Floor(maxY / cellSize) + 1;

            for (int bx = bx0; bx <= bx1; bx++)
            {
                for (int by = by0; by <= by1; by++)
                {
                    if (!buckets.TryGetValue((bx, by), out var list))
                        continue;

                    foreach (int j in list)
                    {
                        if (j > i)
                            pairs.Add((i, j));
                    }
                }
            }
        }

        return pairs;
    }

    private static DesignIssue? CheckPair(SegmentInfo a, SegmentInfo b)
    {
        if (a.PathId == b.PathId)
            return null;

        // The stricter side governs: the pair must respect the larger of the two
        // segments' process minima; a pair where neither side declares one is silent.
        double minSpacing = Math.Max(a.SpacingMicrometers, b.SpacingMicrometers);
        if (minSpacing <= 0)
            return null;

        if (WaveguideSpacingGeometry.SegmentsShareEndpoint(a.Segment, b.Segment))
            return null;

        var (centerDistance, closestPoint) = WaveguideSpacingGeometry.ComputeCenterlineDistance(
            a.Segment, b.Segment, minSpacing);

        if (centerDistance <= WaveguideSpacingGeometry.DistanceToleranceMicrometers)
            return null;

        double edgeDistance = centerDistance - a.HalfWidthMicrometers - b.HalfWidthMicrometers;
        edgeDistance = Math.Max(edgeDistance, 0.0);

        if (edgeDistance >= minSpacing - WaveguideSpacingGeometry.DistanceToleranceMicrometers)
            return null;

        var connection = a.Connection ?? b.Connection;
        string description = string.Create(
            CultureInfo.InvariantCulture,
            $"Waveguides too close: {a.PathLabel} \u2194 {b.PathLabel} (distance {edgeDistance:F2} \u00b5m, minimum {minSpacing:F2} \u00b5m)");

        return new DesignIssue(
            DesignIssueType.WaveguideSpacingViolation,
            connection,
            closestPoint.X,
            closestPoint.Y,
            description);
    }

    private static string FormatConnectionLabel(WaveguideConnection connection)
    {
        var start = $"{connection.StartPin.ParentComponent.Identifier}.{connection.StartPin.Name}";
        var end = $"{connection.EndPin.ParentComponent.Identifier}.{connection.EndPin.Name}";
        return $"{start} \u2192 {end}";
    }
}
