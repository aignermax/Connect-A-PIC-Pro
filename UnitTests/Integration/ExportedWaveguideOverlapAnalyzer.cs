using System.Text.Json;

namespace UnitTests.Integration;

/// <summary>
/// Geometric checker over the JSON emitted by <c>scripts/extract_gds_coords.py</c>:
/// clusters the exported polygons of a design's top cell by geometric contact and
/// proves every routed connection lands in the GDS on its own chain — chains that
/// touch are overlaps, chains missing a pin were dropped. Overlaps located inside
/// component footprints (pin abutments, intentionally placed crossing components)
/// are tolerated; everything else names the offending connection pair so the
/// maintainer learns what failed without opening the viewer.
/// </summary>
public static partial class ExportedWaveguideOverlapAnalyzer
{
    /// <summary>One endpoint of a routed connection, with its expected GDS position (µm).</summary>
    public sealed class Endpoint
    {
        /// <summary>Display name of the connection this endpoint belongs to.</summary>
        public string ConnectionName { get; }
        /// <summary>The pin name and parent component, e.g. "MZI_8.o3".</summary>
        public string PinName { get; }
        /// <summary>Expected exported (nazca) coordinates in µm.</summary>
        public double X { get; }
        /// <summary>Expected exported (nazca) coordinates in µm.</summary>
        public double Y { get; }

        /// <summary>Binds an endpoint.</summary>
        public Endpoint(string connectionName, string pinName, double x, double y)
        {
            ConnectionName = connectionName;
            PinName = pinName;
            X = x;
            Y = y;
        }
    }

    /// <summary>One routed connection identified by its two pins (for violation messages).</summary>
    public sealed class Connection
    {
        /// <summary>Display name, e.g. "MZI_8.o3 → MZI_9.o3".</summary>
        public string Name { get; }

        /// <summary>Start and end pins of the route, in exported (nazca) coordinates.</summary>
        public Endpoint Start { get; }
        public Endpoint End { get; }

        /// <summary>Binds a connection.</summary>
        public Connection(string name, Endpoint start, Endpoint end)
        {
            Name = name;
            Start = start;
            End = end;
        }

        /// <summary>Both endpoints, iterated for coverage.</summary>
        public IEnumerable<Endpoint> Endpoints => new[] { Start, End };
    }

    /// <summary>Axis-aligned rectangle bounding a placed component, in exported coordinates.</summary>
    public readonly struct BoundingBox
    {
        private readonly double _minX, _maxX, _minY, _maxY;

        /// <summary>Binds a rectangle.</summary>
        public BoundingBox(double minX, double maxX, double minY, double maxY)
        {
            _minX = minX; _maxX = maxX; _minY = minY; _maxY = maxY;
        }

        /// <summary>True when the point falls inside the rectangle.</summary>
        public bool Contains(double x, double y) =>
            x >= _minX && x <= _maxX && y >= _minY && y <= _maxY;
    }

    /// <summary>Endpoint coverage tolerance — half a waveguide width is still well inside (µm).</summary>
    private const double EndpointToleranceMicrometers = 1.0;

    /// <summary>Contact tolerance for joining polygons of one routed chain (µm).</summary>
    private const double ContactToleranceMicrometers = 0.02;

    /// <summary>
    /// Returns one violation per offending overlap, dropped chain or broken chain,
    /// naming the exact connection pair. Overlaps that fall inside
    /// <paramref name="allowedRegions"/> (component footprints) are tolerated.
    /// </summary>
    public static List<string> FindViolations(
        string extractionJson,
        string designCellName,
        IReadOnlyList<Connection> connections,
        IReadOnlyList<BoundingBox> allowedRegions)
    {
        var polygons = ParsePolygons(extractionJson, designCellName);
        var violations = new List<string>();
        if (polygons.Count == 0)
        {
            violations.Add($"GDS cell '{designCellName}' exported no polygons at all — " +
                "every routed connection is missing from the artifact.");
            return violations;
        }

        var clusters = BuildClusters(polygons);

        // Resolve every connection to the set of clusters covering its two pins.
        var resolved = new List<(Connection Conn, HashSet<int> Clusters)>();
        foreach (var connection in connections)
        {
            var connClusters = new HashSet<int>();
            var uncoveredPins = new List<Endpoint>();
            foreach (var endpoint in connection.Endpoints)
            {
                var containing = CoveringClusters(clusters, endpoint, EndpointToleranceMicrometers);
                if (containing.Count == 0) uncoveredPins.Add(endpoint);
                foreach (var index in containing) connClusters.Add(index);
            }
            if (uncoveredPins.Count > 0)
                violations.Add(
                    $"Connection '{connection.Name}' exported no waveguide geometry: " +
                    string.Join(" and ", uncoveredPins.Select(p =>
                        $"pin '{p.PinName}' at ({p.X:F2}, {p.Y:F2})")) +
                    " not covered by any exported polygon — its route was dropped from the GDS.");
            else if (connClusters.Count > 1)
                violations.Add($"Connection '{connection.Name}' exported as {connClusters.Count} disjoint " +
                    "geometry chains — a chain must be contiguous end-to-end.");
            if (connClusters.Count > 0) resolved.Add((connection, connClusters));
        }

        // Two connections sharing a cluster is an exported overlap — but a shared
        // chain that lies entirely inside a component footprint is only the pins'
        // abutment (or a placed crossing component) and must not be reported.
        for (int i = 0; i < resolved.Count; i++)
        for (int j = i + 1; j < resolved.Count; j++)
        {
            var shared = resolved[i].Clusters.Intersect(resolved[j].Clusters).ToHashSet();
            if (shared.Count == 0) continue;

            // Every shared chain must be footprint-checked on its own: an
            // out-of-footprint shared chain is an overlap even when another
            // shared chain of the same pair is only a pin abutment.
            var outsideChains = shared
                .Select(index => clusters[index])
                .Where(cluster => !IsFullyInsideFootprints(cluster, allowedRegions))
                .ToList();
            if (outsideChains.Count == 0) continue;

            var centroid = Centroid(outsideChains[0]);
            violations.Add(
                $"Connection '{resolved[i].Conn.Name}' overlaps connection '{resolved[j].Conn.Name}' " +
                $"in the exported GDS (shared geometry chain centroid ({centroid.X:F2}, {centroid.Y:F2}) " +
                "reaches outside the routed component footprints — the #704-class shadow overlap " +
                "this test forbids among the connected pins).");
        }
        return violations;
    }

    /// <summary>Index of clusters whose polygons cover the given point within tolerance.</summary>
    private static HashSet<int> CoveringClusters(List<Cluster> clusters, Endpoint endpoint, double tolerance)
    {
        var result = new HashSet<int>();
        for (var index = 0; index < clusters.Count; index++)
            if (clusters[index].Polygons.Any(p => Covers(p, endpoint.X, endpoint.Y, tolerance)))
                result.Add(index);
        return result;
    }

    /// <summary>Collects the polygons of the named cell from the extraction JSON.</summary>
    private static List<Polygon> ParsePolygons(string json, string designCellName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var polygons = new List<Polygon>();
        if (root.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
        {
            foreach (var cell in cells.EnumerateArray())
            {
                var name = cell.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name != designCellName) continue;
                if (!cell.TryGetProperty("polygons", out var cellPolygons) ||
                    cellPolygons.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var poly in cellPolygons.EnumerateArray())
                {
                    var layer = poly.TryGetProperty("layer", out var l) ? l.GetInt32() : 0;
                    var datatype = poly.TryGetProperty("datatype", out var d) ? d.GetInt32() : 0;
                    var points = new List<Point>();
                    if (poly.TryGetProperty("vertices", out var vertices) &&
                        vertices.ValueKind == JsonValueKind.Array)
                        foreach (var vertex in vertices.EnumerateArray())
                            if (vertex.ValueKind == JsonValueKind.Array && vertex.GetArrayLength() >= 2)
                                points.Add(new Point(vertex[0].GetDouble(), vertex[1].GetDouble()));
                    polygons.Add(new Polygon(layer, datatype, points));
                }
            }
        }
        return polygons;
    }
}
