namespace UnitTests.Integration;

/// <summary>
/// Geometric half of <see cref="ExportedWaveguideOverlapAnalyzer"/>: the polygon model
/// and the predicates that decide coverage, contact and intersection between
/// exported shapes. Split out so each source file stays small — never a second
/// abstraction.
/// </summary>
public static partial class ExportedWaveguideOverlapAnalyzer
{
    /// <summary>One vertex of an exported polygon (µm, exported/nazca coordinates).</summary>
    private readonly struct Point
    {
        public readonly double X, Y;
        public Point(double x, double y) { X = x; Y = y; }
        public double DistanceTo(Point other) =>
            Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
    }

    /// <summary>A GDS boundary of the design cell, layer-tagged.</summary>
    private readonly struct Polygon
    {
        public readonly int Layer;
        public readonly int DataType;
        public readonly IReadOnlyList<Point> Points;
        public Polygon(int layer, int dataType, IReadOnlyList<Point> points)
        {
            Layer = layer;
            DataType = dataType;
            Points = points;
        }
    }

    /// <summary>A geometric chain of touching polygons — one routed connection's export.</summary>
    private sealed class Cluster
    {
        public readonly List<Polygon> Polygons = new();
        public Cluster(Polygon first) => Polygons.Add(first);
    }

    /// <summary>Clusters the given polygons by geometric contact (touch or overlap joins).</summary>
    private static List<Cluster> BuildClusters(List<Polygon> polygons)
    {
        var clusters = new List<Cluster>();
        foreach (var polygon in polygons)
        {
            var touching = clusters
                .Where(cluster => cluster.Polygons.Any(existing => Touch(existing, polygon, ContactToleranceMicrometers)))
                .ToList();
            if (touching.Count == 0)
            {
                clusters.Add(new Cluster(polygon));
                continue;
            }
            touching[0].Polygons.Add(polygon);
            foreach (var excess in touching.Skip(1))
            {
                touching[0].Polygons.AddRange(excess.Polygons);
                clusters.Remove(excess);
            }
        }
        return clusters;
    }

    /// <summary>Geometric mean of every vertex belonging to the cluster.</summary>
    private static Point Centroid(Cluster cluster)
    {
        double sumX = 0, sumY = 0;
        long count = 0;
        foreach (var poly in cluster.Polygons)
        foreach (var pt in poly.Points)
        {
            sumX += pt.X;
            sumY += pt.Y;
            count++;
        }
        return count == 0 ? new Point(0, 0) : new Point(sumX / count, sumY / count);
    }

    /// <summary>True when the point is covered by the polygon within tolerance of its boundary or interior.</summary>
    private static bool Covers(Polygon polygon, double x, double y, double tolerance)
    {
        var candidate = new Point(x, y);
        if (polygon.Points.Any(p => p.DistanceTo(candidate) <= tolerance)) return true;
        for (var i = 0; i < polygon.Points.Count; i++)
        {
            var a = polygon.Points[i];
            var b = polygon.Points[(i + 1) % polygon.Points.Count];
            if (DistancePointToSegment(candidate, a, b) <= tolerance) return true;
        }
        return InteriorContains(polygon.Points, candidate);
    }

    /// <summary>Two polygons touch when any vertex of one lands inside the other or their edges meet.</summary>
    private static bool Touch(Polygon a, Polygon b, double tolerance)
    {
        if (a.Points.Any(p => InteriorContains(b.Points, p)) || b.Points.Any(p => InteriorContains(a.Points, p)))
            return true;

        for (var i = 0; i < a.Points.Count; i++)
            for (var j = 0; j < b.Points.Count; j++)
            {
                var p0 = a.Points[i];
                var p1 = a.Points[(i + 1) % a.Points.Count];
                var q0 = b.Points[j];
                var q1 = b.Points[(j + 1) % b.Points.Count];
                if (SegmentsWithin(p0, p1, q0, q1, tolerance)) return true;
            }
        return false;
    }

    /// <summary>True when the distance between both closed segments is within <paramref name="tolerance"/>.</summary>
    private static bool SegmentsWithin(Point p0, Point p1, Point q0, Point q1, double tolerance)
    {
        if (SegmentsIntersect(p0, p1, q0, q1)) return true;
        return MinDistance(p0, p1, q0, q1) <= tolerance;
    }

    /// <summary>Minimum distance between two closed segments (0 when they cross or overlap).</summary>
    private static double MinDistance(Point p0, Point p1, Point q0, Point q1)
    {
        double shortest = double.MaxValue;
        shortest = Math.Min(shortest, DistancePointToSegment(p0, q0, q1));
        shortest = Math.Min(shortest, DistancePointToSegment(p1, q0, q1));
        shortest = Math.Min(shortest, DistancePointToSegment(q0, p0, p1));
        return Math.Min(shortest, DistancePointToSegment(q1, p0, p1));
    }

    /// <summary>Distance of point to segment (projection clamped to endpoints).</summary>
    private static double DistancePointToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0
            ? 0.0
            : Clamp((p.X - a.X) * dx + (p.Y - a.Y) * dy, 0, lengthSquared) / lengthSquared;
        var projected = new Point(a.X + t * dx, a.Y + t * dy);
        return p.DistanceTo(projected);
    }

    /// <summary>Clamps a value to [min, max].</summary>
    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>Proper segment intersection (sign test; collinear overlap counts too).</summary>
    private static bool SegmentsIntersect(Point p0, Point p1, Point q0, Point q1)
    {
        double Side(Point r, Point a, Point b) =>
            (b.X - a.X) * (r.Y - a.Y) - (b.Y - a.Y) * (r.X - a.X);
        var s1 = Side(q0, p0, p1);
        var s2 = Side(q1, p0, p1);
        var s3 = Side(p0, q0, q1);
        var s4 = Side(p1, q0, q1);
        return (s1 < 0) != (s2 < 0) && (s3 < 0) != (s4 < 0);
    }

    /// <summary>Ray-casting point-in-polygon (boundary covered by the tolerance check).</summary>
    private static bool InteriorContains(IReadOnlyList<Point> polygon, Point point)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if ((a.Y > point.Y) != (b.Y > point.Y))
            {
                var xIntersection = a.X + (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y);
                if (point.X < xIntersection) inside = !inside;
            }
        }
        return inside;
    }
}
