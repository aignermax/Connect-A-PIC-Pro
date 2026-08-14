namespace CAP_Core.Components.Core;

public partial class Component
{
    /// <summary>
    /// Imported outline polygons of the component's physical shape (e.g. from a
    /// GDS-imported PDK component), in app-space coordinates: micrometers, Y-down,
    /// relative to the top-left corner of the unrotated bounding box. When present,
    /// the canvas renders these instead of the plain rectangle body. <c>null</c>
    /// for regular PDK components (legacy rendering). Flows from the component
    /// template on placement and on design load; the list is immutable and shared
    /// across all instances of the same template, so clones safely share the
    /// reference too.
    /// </summary>
    public IReadOnlyList<OutlinePolygon>? OutlinePolygons { get; set; }

    /// <summary>
    /// The footprint dimensions before any rotation (µm) — the frame
    /// <see cref="OutlinePolygons"/> points live in. Zero means "never rotated":
    /// the current <see cref="WidthMicrometers"/>/<see cref="HeightMicrometers"/>
    /// are the unrotated dims. The model-level rotation commands record the
    /// pre-rotation dims here ONCE (they never change afterwards). Rendering
    /// needs them for non-cardinal rotations: the rotated axis-aligned bounding
    /// box no longer reveals the original aspect, so the unrotated geometry
    /// frame cannot be recovered from the live dims alone.
    /// </summary>
    public double UnrotatedWidthMicrometers { get; set; }

    /// <summary>See <see cref="UnrotatedWidthMicrometers"/>.</summary>
    public double UnrotatedHeightMicrometers { get; set; }
}
