namespace CAP_Core.Components.Core;

/// <summary>
/// Model-level pose transforms shared by interactive rotation, programmatic
/// placement (GDS import) and .lun persistence. Lives in the core layer so
/// both CAP.Avalonia and CAP_DataAccess apply the exact same math — a loaded
/// design must reproduce the pin layout the import created.
/// </summary>
public static class ComponentPoseTransform
{
    /// <summary>Tolerance below which two rotation angles count as equal.</summary>
    public const double AngleEpsilonDegrees = 1e-6;

    /// <summary>
    /// Model-level 90° counter-clockwise rotation around the component center:
    /// physical pin offsets, width/height swap, discrete rotation and
    /// <see cref="Component.RotationDegrees"/>. The top-left corner
    /// (<see cref="Component.PhysicalX"/>/<see cref="Component.PhysicalY"/>) is
    /// deliberately left untouched. No ViewModel/canvas notifications — shared
    /// with programmatic placement (GDS import) and .lun loading, which rotate
    /// the component before it is added to the canvas.
    /// </summary>
    public static void Rotate90CounterClockwise(Component component)
    {
        RecordUnrotatedDimensions(component);
        var width = component.WidthMicrometers;
        var height = component.HeightMicrometers;
        var cx = width / 2;
        var cy = height / 2;

        // Pin angles stay relative to the component — GetAbsoluteAngle() adds
        // RotationDegrees, so only the offsets are rotated here.
        foreach (var pin in component.PhysicalPins)
        {
            var x = pin.OffsetXMicrometers - cx;
            var y = pin.OffsetYMicrometers - cy;

            // Rotate 90° counter-clockwise: (x, y) -> (-y, x), then translate
            // back to the swapped-dimension center (cy becomes the new cx).
            pin.OffsetXMicrometers = -y + cy;
            pin.OffsetYMicrometers = x + cx;
        }

        component.WidthMicrometers = height;
        component.HeightMicrometers = width;
        component.RotateBy90CounterClockwise();
    }

    /// <summary>
    /// Model-level rotation around the component center by an ARBITRARY angle,
    /// relative to the current pose, in the same convention
    /// <see cref="Rotate90CounterClockwise"/> uses (0° = east, positive toward
    /// south in the Y-down plane — for θ = 90° the math reduces exactly to a
    /// quarter turn). Physical pin offsets rotate around the old center;
    /// width/height become the axis-aligned bounding box of the rotated
    /// footprint, so an unchanged top-left corner
    /// (<see cref="Component.PhysicalX"/>/<see cref="Component.PhysicalY"/>) is
    /// the rotated AABB's top-left — the same frame the GDS projector computes
    /// for the true instance transform. Pin angles stay relative to the
    /// component (<c>GetAbsoluteAngle()</c> adds
    /// <see cref="Component.RotationDegrees"/>). The discrete tile matrix
    /// (<c>Parts</c>) and <c>Rotation90CounterClock</c> cannot represent
    /// non-cardinal angles and are deliberately left untouched — rendering and
    /// absolute pin angles follow the continuous rotation.
    /// </summary>
    public static void RotateByDegrees(Component component, double degrees)
    {
        RecordUnrotatedDimensions(component);
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var width = component.WidthMicrometers;
        var height = component.HeightMicrometers;
        var cx = width / 2;
        var cy = height / 2;

        // Axis-aligned bounding box of the rotated footprint.
        var newWidth = width * Math.Abs(cos) + height * Math.Abs(sin);
        var newHeight = width * Math.Abs(sin) + height * Math.Abs(cos);

        foreach (var pin in component.PhysicalPins)
        {
            var x = pin.OffsetXMicrometers - cx;
            var y = pin.OffsetYMicrometers - cy;
            pin.OffsetXMicrometers = x * cos - y * sin + newWidth / 2;
            pin.OffsetYMicrometers = x * sin + y * cos + newHeight / 2;
        }

        component.WidthMicrometers = newWidth;
        component.HeightMicrometers = newHeight;
        component.RotationDegrees = NormalizeDegrees(component.RotationDegrees + degrees);
    }

    /// <summary>
    /// Brings the component from its current (cardinal) rotation to the exact
    /// continuous angle <paramref name="exactRotationDegrees"/>: the shortest
    /// signed residual is applied via <see cref="RotateByDegrees"/> (pins
    /// re-based into the rotated footprint's AABB) and
    /// <see cref="Component.RotationDegrees"/> takes the exact value. Used by
    /// GDS placement and on .lun load to restore instances placed at
    /// non-cardinal angles (e.g. 330°).
    /// </summary>
    public static void ApplyExactRotation(Component component, double exactRotationDegrees)
    {
        var residual = NormalizeDegrees(exactRotationDegrees - component.RotationDegrees);
        if (residual > 180)
            residual -= 360; // Shortest signed residual, e.g. 350 -> -10.

        if (Math.Abs(residual) > AngleEpsilonDegrees)
            RotateByDegrees(component, residual);

        component.RotationDegrees = NormalizeDegrees(exactRotationDegrees);
    }

    /// <summary>
    /// Mirrors the physical pins across the component's horizontal centerline in
    /// its LOCAL (unrotated) frame: offset Y flips within the box, the angle maps
    /// θ → −θ (a down-pointing pin becomes up-pointing). This is the app-space
    /// effect of the GDS STRANS flag (reflection across the GDS x-axis) on pins.
    /// Geometry (parts, outlines) is NOT mirrored: the core model has no mirror
    /// support (v1 limitation). Toggles <see cref="Component.IsMirroredHorizontally"/>
    /// so persistence can re-apply the mirror on load.
    /// </summary>
    public static void MirrorPinsHorizontally(Component component)
    {
        foreach (var pin in component.PhysicalPins)
        {
            pin.OffsetYMicrometers = component.HeightMicrometers - pin.OffsetYMicrometers;
            pin.AngleDegrees = (360.0 - pin.AngleDegrees) % 360.0;
        }
        component.IsMirroredHorizontally = !component.IsMirroredHorizontally;
    }

    /// <summary>
    /// The component's continuous rotation when it differs from its discrete
    /// quarter-turn rotation, otherwise null. Persistence writes this into the
    /// optional <c>RotationDegrees</c> field so cardinal-only designs keep the
    /// compact legacy format.
    /// </summary>
    public static double? GetNonCardinalRotationDegrees(Component component)
    {
        var cardinal = (int)component.Rotation90CounterClock * 90.0;
        var delta = NormalizeDegrees(component.RotationDegrees - cardinal);
        if (delta > 180)
            delta = 360 - delta;
        return delta > AngleEpsilonDegrees ? component.RotationDegrees : null;
    }

    /// <summary>
    /// Records the pre-rotation footprint dims ONCE as the unrotated outline
    /// frame (<see cref="Component.UnrotatedWidthMicrometers"/>). Idempotent by
    /// design: the unrotated frame is invariant under further rotations, and
    /// undo/redo cycles must never overwrite it with already-rotated dims.
    /// </summary>
    private static void RecordUnrotatedDimensions(Component component)
    {
        if (component.UnrotatedWidthMicrometers <= 0 || component.UnrotatedHeightMicrometers <= 0)
        {
            component.UnrotatedWidthMicrometers = component.WidthMicrometers;
            component.UnrotatedHeightMicrometers = component.HeightMicrometers;
        }
    }

    /// <summary>Normalizes an angle to the range [0, 360).</summary>
    private static double NormalizeDegrees(double degrees) => ((degrees % 360) + 360) % 360;
}
