namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Uniform-grid spatial hash for the import matchers: items (pin points or
/// polygon bounding boxes) are bucketed by position so a query only tests the
/// handful of candidates near the query box instead of every item
/// (<see cref="GdsRouteConnectivityMatcher"/> is otherwise O(polygons × pins)
/// for the pin-touch scan and O(polygons²) for network building — tens of
/// seconds on a production-scale file). The grid is build-once/query-many and
/// single-threaded; candidates are returned deduplicated but unordered — the
/// caller sorts to restore its deterministic scan order.
/// </summary>
internal sealed class GdsSpatialGrid
{
    private readonly double _cellSize;
    private readonly Dictionary<(int CellX, int CellY), List<int>> _buckets = new();

    // Deduplication stamps: an item inserted into several cells would otherwise
    // appear once per overlapped cell in every query result.
    private readonly int[] _lastSeenStamp;
    private int _stamp;

    private GdsSpatialGrid(double cellSize, int capacity)
    {
        _cellSize = cellSize;
        _lastSeenStamp = new int[capacity];
    }

    /// <summary>
    /// Creates a grid whose cell size adapts to the data: the overall item span
    /// divided into 64 cells per axis, but never below twice the query tolerance
    /// (a cell smaller than the tolerance only multiplies bucket lookups) and
    /// never zero (a single-point data set has no span at all).
    /// </summary>
    /// <param name="spanUm">Largest of the overall item bbox's width/height (µm; 0 allowed).</param>
    /// <param name="toleranceUm">The query expansion the grid will serve (µm).</param>
    /// <param name="capacity">Number of items that will be inserted (indexes must stay below).</param>
    public static GdsSpatialGrid Create(double spanUm, double toleranceUm, int capacity) =>
        new(Math.Max(Math.Max(spanUm / 64.0, toleranceUm * 2.0), 1e-6), capacity);

    /// <summary>Inserts a point item.</summary>
    public void InsertPoint(int index, double x, double y) => InsertBox(index, x, y, x, y);

    /// <summary>Inserts an item occupying the given axis-aligned box.</summary>
    public void InsertBox(int index, double minX, double minY, double maxX, double maxY)
    {
        var (x0, y0) = Cell(minX, minY);
        var (x1, y1) = Cell(maxX, maxY);
        for (var cx = x0; cx <= x1; cx++)
        {
            for (var cy = y0; cy <= y1; cy++)
            {
                if (!_buckets.TryGetValue((cx, cy), out var bucket))
                    _buckets.Add((cx, cy), bucket = new List<int>());
                bucket.Add(index);
            }
        }
    }

    /// <summary>
    /// The indexes of all items whose inserted box overlaps the query box,
    /// deduplicated, unordered. This is a superset test: whether an item truly
    /// qualifies (touch, distance) is the caller's own predicate.
    /// </summary>
    public List<int> QueryBox(double minX, double minY, double maxX, double maxY)
    {
        var result = new List<int>();
        _stamp++;
        var (x0, y0) = Cell(minX, minY);
        var (x1, y1) = Cell(maxX, maxY);
        for (var cx = x0; cx <= x1; cx++)
        {
            for (var cy = y0; cy <= y1; cy++)
            {
                if (!_buckets.TryGetValue((cx, cy), out var bucket))
                    continue;
                foreach (var index in bucket)
                {
                    if (_lastSeenStamp[index] == _stamp)
                        continue;
                    _lastSeenStamp[index] = _stamp;
                    result.Add(index);
                }
            }
        }
        return result;
    }

    private (int CellX, int CellY) Cell(double x, double y) =>
        ((int)Math.Floor(x / _cellSize), (int)Math.Floor(y / _cellSize));
}
