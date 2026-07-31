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
}
