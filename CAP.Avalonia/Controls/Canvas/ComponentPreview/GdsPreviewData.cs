using Avalonia.Media.Imaging;
using CAP_Core.Export;

namespace CAP.Avalonia.Controls.Canvas.ComponentPreview;

/// <summary>
/// Immutable snapshot of GDS polygon data ready for canvas rendering.
/// Holds the raw Nazca preview result together with the component dimensions
/// that were used to build the coordinate-space transform.
/// </summary>
/// <param name="Result">Raw result from the Nazca/KLayout preview script.</param>
/// <param name="WidthMicrometers">Unrotated component width in µm (RotationDegrees = 0 frame) at the time of fetch.</param>
/// <param name="HeightMicrometers">Unrotated component height in µm (RotationDegrees = 0 frame) at the time of fetch.</param>
public sealed record GdsPreviewData(
    NazcaPreviewResult Result,
    double WidthMicrometers,
    double HeightMicrometers)
{
    /// <summary>
    /// Pre-rasterised bitmap created on the UI thread after polygon fetch.
    /// Null only during the brief window between cache population and bitmap creation.
    /// When non-null, <see cref="GdsPolygonRenderer.DrawGdsPreview"/> blits this
    /// directly instead of rebuilding geometry.
    /// </summary>
    public RenderTargetBitmap? Bitmap { get; init; }
}
