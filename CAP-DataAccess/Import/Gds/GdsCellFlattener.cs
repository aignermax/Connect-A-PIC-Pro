namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// 2D affine transform as a 2×3 matrix mapping (x, y) to
/// (A·x + B·y + C, D·x + E·y + F). Used by <see cref="GdsCellFlattener"/> to
/// push cell-reference transforms down the hierarchy.
/// </summary>
internal readonly record struct GdsTransform(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity transform.</summary>
    public static GdsTransform Identity => new(1, 0, 0, 0, 1, 0);

    /// <summary>Applies the transform to a point.</summary>
    public GdsPoint Apply(GdsPoint p) => new(A * p.X + B * p.Y + C, D * p.X + E * p.Y + F);

    /// <summary>
    /// Returns the composition that first applies this transform and then
    /// <paramref name="outer"/> — i.e. how a point already expressed in a child
    /// cell's space is carried further up into the parent's space.
    /// </summary>
    public GdsTransform Then(GdsTransform outer) =>
        new(
            outer.A * A + outer.B * D,
            outer.A * B + outer.B * E,
            outer.A * C + outer.B * F + outer.C,
            outer.D * A + outer.E * D,
            outer.D * B + outer.E * E,
            outer.D * C + outer.E * F + outer.F);

    /// <summary>
    /// Builds the transform for one expanded instance of a reference: GDS
    /// semantics are magnification and X-reflection first, then the
    /// counter-clockwise rotation, then the translation. Array lattice vectors
    /// are rotated (and mirrored) but not magnified — the spacings stored in the
    /// file are already final — matching how mainstream writers (gdspy, gdstk,
    /// KLayout) emit AREF XY points.
    /// </summary>
    public static GdsTransform FromReference(GdsReference reference, int column, int row)
    {
        double radians = reference.AngleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double m = reference.Magnification;
        double ySign = reference.Reflected ? -1.0 : 1.0;

        // Linear part: rotation · reflection · uniform scale.
        double a = cos * m;
        double b = -sin * ySign * m;
        double d = sin * m;
        double e = cos * ySign * m;

        // Array lattice vector of this instance, rotated/mirrored (not scaled).
        double lx = column * reference.ColumnSpacingMicrometers;
        double ly = ySign * row * reference.RowSpacingMicrometers;
        double tx = reference.Offset.X + cos * lx - sin * ly;
        double ty = reference.Offset.Y + sin * lx + cos * ly;

        return new GdsTransform(a, b, tx, d, e, ty);
    }
}

/// <summary>
/// Resolves the cell hierarchy of a <see cref="GdsLibrary"/>: applies SREF/AREF
/// transforms recursively, expands arrays, computes bounding boxes and exposes
/// the direct instance tree of a top cell. All output coordinates are
/// micrometers with the GDS-native Y-up orientation preserved — flipping Y for
/// the application's coordinate system is the caller's job.
/// </summary>
public sealed class GdsCellFlattener
{
    private readonly GdsLibrary _library;

    /// <summary>Creates a flattener over a parsed library.</summary>
    public GdsCellFlattener(GdsLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <summary>
    /// Returns all polygons and texts of <paramref name="cellName"/>, including
    /// everything pulled in through (nested) references, transformed into the
    /// cell's own coordinate space. Paths are centerline geometry and are not
    /// converted to polygons here — consumers build outlines from the model.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Unknown cell name, reference to an undefined cell, or a reference cycle.
    /// </exception>
    public FlattenedGdsCell Flatten(string cellName)
    {
        var result = new FlattenedGdsCell { CellName = cellName };
        FlattenInto(cellName, GdsTransform.Identity, result.Polygons, result.Texts, new Stack<string>());
        return result;
    }

    /// <summary>
    /// Bounding box in micrometers over all elements of the cell (polygons,
    /// paths including their width, text anchors) with all references resolved.
    /// A cell without geometry yields <see cref="GdsBoundingBox.Empty"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Unknown cell name, reference to an undefined cell, or a reference cycle.
    /// </exception>
    public GdsBoundingBox GetBoundingBox(string cellName)
    {
        var box = new BoundingBoxAccumulator();
        AccumulateBoundingBox(cellName, GdsTransform.Identity, box, new Stack<string>());
        return box.BoundingBox;
    }

    /// <summary>
    /// The direct child instances of <paramref name="topCellName"/>: one entry
    /// per SREF and one per expanded AREF member, each with its transform
    /// resolved into the top cell's coordinate space. The referenced cells
    /// themselves are NOT flattened — hierarchy-aware importers walk the tree
    /// themselves.
    /// </summary>
    /// <exception cref="InvalidDataException">Unknown cell name.</exception>
    public IReadOnlyList<GdsInstance> GetInstanceTree(string topCellName)
    {
        var cell = GetCell(topCellName);
        var instances = new List<GdsInstance>();

        foreach (var reference in cell.Elements.OfType<GdsReference>())
        {
            for (int row = 0; row < reference.Rows; row++)
            {
                for (int column = 0; column < reference.Columns; column++)
                {
                    var transform = GdsTransform.FromReference(reference, column, row);
                    instances.Add(new GdsInstance
                    {
                        CellName = reference.CellName,
                        Offset = transform.Apply(new GdsPoint(0, 0)),
                        AngleDegrees = reference.AngleDegrees,
                        Magnification = reference.Magnification,
                        Reflected = reference.Reflected,
                    });
                }
            }
        }

        return instances;
    }

    // ── Recursive walks ──────────────────────────────────────────────────────

    private void FlattenInto(
        string cellName,
        GdsTransform transform,
        List<GdsPolygon> polygons,
        List<GdsText> texts,
        Stack<string> path)
    {
        var cell = EnterCell(cellName, path);

        foreach (var element in cell.Elements)
        {
            switch (element)
            {
                case GdsPolygon polygon:
                    polygons.Add(polygon with { Points = polygon.Points.Select(transform.Apply).ToList() });
                    break;

                case GdsText text:
                    texts.Add(TransformText(text, transform));
                    break;

                case GdsReference reference:
                    for (int row = 0; row < reference.Rows; row++)
                    {
                        for (int column = 0; column < reference.Columns; column++)
                        {
                            var instanceTransform = GdsTransform.FromReference(reference, column, row).Then(transform);
                            FlattenInto(reference.CellName, instanceTransform, polygons, texts, path);
                        }
                    }
                    break;

                // GdsPath: kept as centerline + width in the model; outline
                // generation is a consumer concern (different end-cap styles).
            }
        }

        path.Pop();
    }

    private void AccumulateBoundingBox(
        string cellName,
        GdsTransform transform,
        BoundingBoxAccumulator box,
        Stack<string> path)
    {
        var cell = EnterCell(cellName, path);

        foreach (var element in cell.Elements)
        {
            switch (element)
            {
                case GdsPolygon polygon:
                    foreach (var point in polygon.Points)
                        box.Include(transform.Apply(point));
                    break;

                case GdsPath gdsPath:
                    // Stroke extent: centerline ± half width in every direction —
                    // conservative for round/extended caps, exact for flush caps.
                    double scale = Math.Sqrt(transform.A * transform.A + transform.D * transform.D);
                    double halfWidth = gdsPath.WidthMicrometers * scale / 2.0;
                    foreach (var point in gdsPath.Points)
                    {
                        var p = transform.Apply(point);
                        box.Include(new GdsPoint(p.X - halfWidth, p.Y - halfWidth));
                        box.Include(new GdsPoint(p.X + halfWidth, p.Y + halfWidth));
                    }
                    break;

                case GdsText text:
                    box.Include(transform.Apply(text.Position));
                    break;

                case GdsReference reference:
                    for (int row = 0; row < reference.Rows; row++)
                    {
                        for (int column = 0; column < reference.Columns; column++)
                        {
                            var instanceTransform = GdsTransform.FromReference(reference, column, row).Then(transform);
                            AccumulateBoundingBox(reference.CellName, instanceTransform, box, path);
                        }
                    }
                    break;
            }
        }

        path.Pop();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private GdsCell GetCell(string cellName) =>
        _library.Cells.TryGetValue(cellName, out var cell)
            ? cell
            : throw new InvalidDataException($"GDS cell '{cellName}' is referenced but not defined in the library.");

    /// <summary>Cycle guard: entering a cell already on the current path is a reference cycle.</summary>
    private GdsCell EnterCell(string cellName, Stack<string> path)
    {
        var cell = GetCell(cellName);
        if (path.Contains(cellName))
        {
            string cycle = string.Join(" → ", path.Reverse().Append(cellName));
            throw new InvalidDataException($"Cyclic GDS cell reference detected: {cycle}.");
        }
        path.Push(cellName);
        return cell;
    }

    /// <summary>
    /// Moves a text into the parent space. The transform's rotation is extracted
    /// from the linear part; when the transform mirrors (negative determinant)
    /// the text angle flips sign, matching the mirrored baseline direction.
    /// </summary>
    private static GdsText TransformText(GdsText text, GdsTransform transform)
    {
        double transformAngle = Math.Atan2(transform.D, transform.A) * 180.0 / Math.PI;
        bool mirrored = transform.A * transform.E - transform.B * transform.D < 0;
        double angle = mirrored
            ? transformAngle - text.AngleDegrees
            : transformAngle + text.AngleDegrees;
        return text with { Position = transform.Apply(text.Position), AngleDegrees = angle };
    }

    private sealed class BoundingBoxAccumulator
    {
        private double _minX = double.PositiveInfinity;
        private double _minY = double.PositiveInfinity;
        private double _maxX = double.NegativeInfinity;
        private double _maxY = double.NegativeInfinity;

        public bool IsEmpty { get; private set; } = true;

        public GdsBoundingBox BoundingBox =>
            IsEmpty ? GdsBoundingBox.Empty : new GdsBoundingBox(_minX, _minY, _maxX, _maxY);

        public void Include(GdsPoint point)
        {
            IsEmpty = false;
            _minX = Math.Min(_minX, point.X);
            _minY = Math.Min(_minY, point.Y);
            _maxX = Math.Max(_maxX, point.X);
            _maxY = Math.Max(_maxY, point.Y);
        }
    }
}
