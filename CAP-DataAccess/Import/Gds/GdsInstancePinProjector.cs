namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// A cell pin projected into the application's circuit space: micrometers,
/// Y-down, origin at the top-left corner of the top-cell bounding box.
/// </summary>
internal sealed record GdsAbsolutePin
{
    public string Name { get; init; } = string.Empty;

    public double XUm { get; init; }

    public double YUm { get; init; }

    /// <summary>Outward direction in degrees, app convention (0° = east, 90° = down in the Y-down plane).</summary>
    public double AngleDegrees { get; init; }

    /// <summary>
    /// Signal domain of the source pin: <c>true</c>/<c>false</c> for pins of known
    /// PDK components (from the template), <c>null</c> for geometry-detected pins
    /// (kind unknown — see <see cref="DetectedPin.IsElectrical"/>).
    /// </summary>
    public bool? IsElectrical { get; init; }
}

/// <summary>
/// The pin-anchored placement frame of a known-resolved cell: the template's
/// bounding box re-located inside the GDS cell's bbox frame (for
/// <see cref="GdsInstancePinProjector.ProjectPlacedBoundsTopLeft"/>) and the
/// template pins shifted by the same delta (for
/// <see cref="GdsInstancePinProjector.ProjectPins"/>), so both placement and
/// projected pins follow the cell's PIN LABELS instead of its bounding box —
/// foundry cells routinely carry marker geometry (e.g. SiEPIC m_pin paths) that
/// inflates the bbox past the template without moving a single pin.
/// </summary>
internal sealed record KnownCellPinAnchor
{
    /// <summary>The template's bbox re-located into the GDS cell's coordinate frame (GDS, Y-up).</summary>
    public required GdsBoundingBox PlacementBox { get; init; }

    /// <summary>The template's pins shifted by the anchor delta, in app-space of the CELL bbox.</summary>
    public required IReadOnlyList<DetectedPin> ShiftedPins { get; init; }

    /// <summary>Largest per-pin distance (µm) between a matched pin's delta and the mean delta; 0 for a single matched pin.</summary>
    public required double MaxDeviationUm { get; init; }

    /// <summary>Name of the pin with the largest deviation (empty for a single matched pin).</summary>
    public required string WorstPinName { get; init; }
}

/// <summary>
/// Projects cell-local pins and bounding boxes through an instance transform
/// into top-cell app space. The projection always uses the TRUE GDS transform
/// (reflection, exact angle and magnification included, via the same linear
/// part as <see cref="GdsTransform.FromReference"/>), so the results are honest
/// routing data even when the placed visual is approximated (reflection dropped,
/// angle snapped).
///
/// The math per pin, with <c>cb</c> = cell bbox (GDS, Y-up), <c>tb</c> = top
/// bbox (GDS, Y-up), T = instance transform:
/// <list type="number">
/// <item>app-space pin → cell GDS space: <c>gx = px + cb.MinX</c>,
/// <c>gy = cb.MaxY − py</c> (exact inverse of the pin detector's mapping).</item>
/// <item>through the instance transform: <c>P′ = T(g)</c>.</item>
/// <item>into app space: <c>ax = P′.X − tb.MinX</c>, <c>ay = tb.MaxY − P′.Y</c>.</item>
/// </list>
/// Angles travel through the transform's linear part: an app-space direction
/// <c>(cos θ, sin θ)</c> (Y-down) is <c>(cos θ, −sin θ)</c> in GDS (Y-up)
/// space, gets multiplied by the linear part, and is flipped back.
/// </summary>
internal static class GdsInstancePinProjector
{
    /// <summary>
    /// Computes the pin-anchored placement frame for a cell resolved to a known
    /// component: the pure translation that best maps the template's pin offsets
    /// onto the cell's detected pin-LABEL positions. Both frames are app-space
    /// with the origin at their bbox top-left, so the same component placed in
    /// the GDS differs from the template only by the bbox framing — foundry
    /// marker geometry (e.g. SiEPIC m_pin paths) inflates the cell bbox without
    /// moving the pins, and the labels remain the authoritative pin evidence.
    /// Returns null when not a single template pin has a same-named label on
    /// the cell — the caller then keeps the bbox frame (and its size-mismatch
    /// warning).
    /// <para>
    /// The anchor is a TRANSLATION only: the placement rotation stays the
    /// instance's STRANS-derived cardinal. A pin layout rotated or genuinely
    /// resized inside the cell cannot be absorbed by a translation — it spreads
    /// the per-pin deltas, which <see cref="KnownCellPinAnchor.MaxDeviationUm"/>
    /// surfaces (the caller warns past its tolerance).
    /// </para>
    /// </summary>
    /// <param name="known">The resolved known component (template pins authoritative).</param>
    /// <param name="cellPins">
    /// The cell's detected pins in app-space of <paramref name="cellBBox"/>
    /// (label + edge-heuristic pins; only label pins participate).
    /// </param>
    /// <param name="cellBBox">The GDS cell's bounding box (GDS, Y-up).</param>
    public static KnownCellPinAnchor? AnchorToTemplatePins(
        KnownComponent known,
        IReadOnlyList<DetectedPin> cellPins,
        GdsBoundingBox cellBBox)
    {
        var labelsByName = new Dictionary<string, DetectedPin>(StringComparer.Ordinal);
        foreach (var pin in cellPins)
        {
            if (pin.Source == DetectedPinSource.Label)
                labelsByName.TryAdd(pin.Name, pin);
        }

        double sumX = 0, sumY = 0;
        var deltas = new List<(string PinName, double Dx, double Dy)>();
        foreach (var templatePin in known.Pins)
        {
            if (!labelsByName.TryGetValue(templatePin.Name, out var label))
                continue;
            deltas.Add((templatePin.Name, label.XUm - templatePin.XUm, label.YUm - templatePin.YUm));
            sumX += label.XUm - templatePin.XUm;
            sumY += label.YUm - templatePin.YUm;
        }
        if (deltas.Count == 0)
            return null;

        double dx = sumX / deltas.Count;
        double dy = sumY / deltas.Count;
        double maxDeviation = 0;
        var worstPin = string.Empty;
        foreach (var (pinName, ddx, ddy) in deltas)
        {
            var deviation = Math.Sqrt((ddx - dx) * (ddx - dx) + (ddy - dy) * (ddy - dy));
            if (deviation > maxDeviation)
            {
                maxDeviation = deviation;
                worstPin = pinName;
            }
        }

        // The template's bbox re-located into the GDS cell's (Y-up) coordinate
        // frame: the template pin at offset t sits at t + (dx, dy) in cell-bbox
        // app space, so the template's own top-left corner lands at (dx, dy)
        // there — (MinX + dx, MaxY − dy) in GDS coordinates.
        var placementBox = new GdsBoundingBox(
            cellBBox.MinX + dx,
            cellBBox.MaxY - dy - known.HeightUm,
            cellBBox.MinX + dx + known.WidthUm,
            cellBBox.MaxY - dy);
        var shiftedPins = known.Pins
            .Select(p => p with { XUm = p.XUm + dx, YUm = p.YUm + dy })
            .ToList();
        return new KnownCellPinAnchor
        {
            PlacementBox = placementBox,
            ShiftedPins = shiftedPins,
            MaxDeviationUm = maxDeviation,
            WorstPinName = worstPin,
        };
    }

    /// <summary>
    /// Projects <paramref name="cellPins"/> (app-space of the cell's own bbox,
    /// as <see cref="GdsPinDetector"/> emits them) through the instance
    /// transform into top-cell app space.
    /// </summary>
    public static IReadOnlyList<GdsAbsolutePin> ProjectPins(
        GdsInstance instance,
        GdsBoundingBox cellBBox,
        IReadOnlyList<DetectedPin> cellPins,
        GdsBoundingBox topBBox)
    {
        var transform = TrueTransform(instance);
        var result = new List<GdsAbsolutePin>(cellPins.Count);
        foreach (var pin in cellPins)
        {
            var gds = new GdsPoint(pin.XUm + cellBBox.MinX, cellBBox.MaxY - pin.YUm);
            var placed = transform.Apply(gds);

            double radians = pin.AngleDegrees * Math.PI / 180.0;
            double dirGdsX = Math.Cos(radians);
            double dirGdsY = -Math.Sin(radians);
            double dirX = transform.A * dirGdsX + transform.B * dirGdsY;
            double dirY = transform.D * dirGdsX + transform.E * dirGdsY;

            result.Add(new GdsAbsolutePin
            {
                Name = pin.Name,
                XUm = placed.X - topBBox.MinX,
                YUm = topBBox.MaxY - placed.Y,
                AngleDegrees = Normalize360(Math.Atan2(-dirY, dirX) * 180.0 / Math.PI),
                IsElectrical = pin.IsElectrical,
            });
        }
        return result;
    }

    /// <summary>
    /// App-space top-left corner of the instance's placed axis-aligned bounding
    /// box: the cell bbox corners pushed through the true transform, then
    /// Y-flipped into top-cell app space.
    /// </summary>
    public static GdsPoint ProjectPlacedBoundsTopLeft(
        GdsInstance instance, GdsBoundingBox cellBBox, GdsBoundingBox topBBox)
    {
        var transform = TrueTransform(instance);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (x, y) in new[]
                 {
                     (cellBBox.MinX, cellBBox.MinY), (cellBBox.MaxX, cellBBox.MinY),
                     (cellBBox.MinX, cellBBox.MaxY), (cellBBox.MaxX, cellBBox.MaxY),
                 })
        {
            var p = transform.Apply(new GdsPoint(x, y));
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }
        return new GdsPoint(minX - topBBox.MinX, topBBox.MaxY - maxY);
    }

    /// <summary>
    /// The instance's true GDS-space transform: X-reflection and magnification
    /// first, then the counter-clockwise rotation, then the translation — the
    /// same construction <see cref="GdsTransform.FromReference"/> uses.
    /// <see cref="GdsInstance.Offset"/> already holds the resolved translation.
    /// Also used by <see cref="GdsExportArtifactExpander"/> when it composes an
    /// instance's transform with the references inside an expanded export
    /// artifact.
    /// </summary>
    internal static GdsTransform TrueTransform(GdsInstance instance)
    {
        double radians = instance.AngleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double m = instance.Magnification;
        double ySign = instance.Reflected ? -1.0 : 1.0;
        return new GdsTransform(
            cos * m, -sin * ySign * m, instance.Offset.X,
            sin * m, cos * ySign * m, instance.Offset.Y);
    }

    /// <summary>Normalizes an angle in degrees to [0, 360).</summary>
    internal static double Normalize360(double degrees)
    {
        double result = degrees % 360.0;
        return result < 0 ? result + 360.0 : result;
    }

    /// <summary>Normalizes an angle difference in degrees to (−180, 180].</summary>
    internal static double Normalize180(double degrees)
    {
        double result = Normalize360(degrees);
        return result > 180.0 ? result - 360.0 : result;
    }
}
