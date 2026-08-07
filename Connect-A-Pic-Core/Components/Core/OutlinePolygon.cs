using System.Text.Json.Serialization;

namespace CAP_Core.Components.Core;

/// <summary>
/// A 2D vertex of an <see cref="OutlinePolygon"/> in micrometers, app-space
/// convention: Y axis points down, origin at the top-left corner of the
/// component's (unrotated) bounding box.
/// </summary>
public readonly record struct OutlinePoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

/// <summary>
/// One closed outline polygon of a component's physical shape (e.g. imported
/// from GDS): a ring of vertices with the first point repeated at the end
/// (GDS convention), in app-space coordinates — micrometers, Y-down, relative
/// to the top-left corner of the component's unrotated bounding box.
/// Layer/datatype are kept so future renderers can style per layer; v1 draws
/// all layers with a single style.
/// </summary>
/// <remarks>
/// This type doubles as the PDK JSON DTO (<c>PdkComponentDraft.OutlinePolygons</c>)
/// so outlines flow unchanged from the PDK file through the component template
/// onto placed <see cref="Component"/> instances.
/// </remarks>
public sealed record OutlinePolygon
{
    /// <summary>GDS layer number the polygon came from.</summary>
    [JsonPropertyName("layer")]
    public int Layer { get; init; }

    /// <summary>GDS datatype the polygon came from.</summary>
    [JsonPropertyName("dataType")]
    public int DataType { get; init; }

    /// <summary>Closed ring of vertices (first point repeated at the end).</summary>
    [JsonPropertyName("points")]
    public IReadOnlyList<OutlinePoint> Points { get; init; } = Array.Empty<OutlinePoint>();
}
