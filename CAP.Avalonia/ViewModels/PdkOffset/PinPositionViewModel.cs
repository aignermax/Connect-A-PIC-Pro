namespace CAP.Avalonia.ViewModels.PdkOffset;

/// <summary>
/// Display data for a single physical pin in the PDK Offset Editor.
/// Shows the pin's local-component coordinates and its position relative
/// to the Nazca origin (which depends on the current NazcaOriginOffset).
/// </summary>
public class PinPositionViewModel
{
    /// <summary>Pin name (e.g. "a0", "b1").</summary>
    public string PinName { get; }

    /// <summary>Pin X position in component-local µm (from the PDK JSON).</summary>
    public double LocalX { get; }

    /// <summary>Pin Y position in component-local µm (from the PDK JSON).</summary>
    public double LocalY { get; }

    /// <summary>
    /// Pin X position relative to the Nazca cell origin.
    /// Computed as: LocalX - NazcaOriginOffsetX
    /// </summary>
    public double NazcaRelX { get; }

    /// <summary>
    /// Pin Y position relative to the Nazca cell origin in Nazca coordinate space.
    /// Computed as: NazcaOriginOffsetY - LocalY (mapper convention: the offset is
    /// the bbox top edge above the cell org, and Nazca's Y axis is flipped
    /// relative to Lunima's y-down screen space).
    /// </summary>
    public double NazcaRelY { get; }

    /// <summary>Formatted string for table display.</summary>
    public string LocalXText => $"{LocalX:F3} µm";

    /// <summary>Formatted string for table display.</summary>
    public string LocalYText => $"{LocalY:F3} µm";

    /// <summary>Formatted string for table display.</summary>
    public string NazcaXText => $"{NazcaRelX:F3} µm";

    /// <summary>Formatted string for table display.</summary>
    public string NazcaYText => $"{NazcaRelY:F3} µm";

    /// <summary>
    /// Initializes pin position data given local coordinates and the component's
    /// current NazcaOriginOffset and height.
    /// </summary>
    /// <param name="pinName">Pin identifier.</param>
    /// <param name="localX">Pin X offset in µm from component origin.</param>
    /// <param name="localY">Pin Y offset in µm from component origin.</param>
    /// <param name="nazcaOffsetX">Current NazcaOriginOffsetX in µm.</param>
    /// <param name="nazcaOffsetY">Current NazcaOriginOffsetY in µm.</param>
    public PinPositionViewModel(
        string pinName,
        double localX,
        double localY,
        double nazcaOffsetX,
        double nazcaOffsetY)
    {
        PinName = pinName;
        LocalX = localX;
        LocalY = localY;
        NazcaRelX = localX - nazcaOffsetX;
        NazcaRelY = nazcaOffsetY - localY;
    }
}

/// <summary>
/// Position of a pin marker on the visual canvas overlay.
/// </summary>
/// <param name="Name">Pin name for the tooltip label.</param>
/// <param name="CanvasX">X coordinate in canvas pixels.</param>
/// <param name="CanvasY">Y coordinate in canvas pixels.</param>
public record PinMarker(string Name, double CanvasX, double CanvasY);

/// <summary>
/// Canvas-space polygon from the Nazca GDS overlay (dark green).
/// </summary>
/// <param name="Layer">GDS layer number.</param>
/// <param name="CanvasPoints">Polygon vertices in canvas pixel coordinates.</param>
public record NazcaPolygonMarker(int Layer, IReadOnlyList<(double X, double Y)> CanvasPoints);

/// <summary>
/// Canvas-space pin stub line from the Nazca GDS overlay (cyan).
/// </summary>
/// <param name="Name">Pin name for tooltip.</param>
/// <param name="X0">Stub start X in canvas pixels.</param>
/// <param name="Y0">Stub start Y in canvas pixels.</param>
/// <param name="X1">Stub end X in canvas pixels.</param>
/// <param name="Y1">Stub end Y in canvas pixels.</param>
public record NazcaStubMarker(string Name, double X0, double Y0, double X1, double Y1);
