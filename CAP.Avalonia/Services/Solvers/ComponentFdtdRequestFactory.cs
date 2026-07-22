using CAP.Avalonia.Services.GdsFactoryExport;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

/// <summary>
/// Builds an <see cref="FdtdSMatrixRequest"/> for a placed component by rendering
/// its geometry (reusing <see cref="NazcaComponentPreviewService"/>, the same
/// single-component renderer the PDK Offset Editor uses — or the gdsfactory
/// renderer for gdsfactory-native components like CornerStone) and turning the
/// rendered polygons and pin stubs into FDTD geometry and ports. Lunima knows
/// its own pins, so ports come from the render rather than being reconstructed.
/// </summary>
public class ComponentFdtdRequestFactory
{
    /// <summary>Default port (waveguide) width in µm when the render doesn't carry one.</summary>
    public const double DefaultPortWidthUm = 0.5;

    /// <summary>
    /// Default GDS layer carrying the optical waveguide. The render returns many
    /// layers (metal, dummy, design-area); FDTD must only see the guiding layer,
    /// so polygons are filtered to this layer (with a fall-back to all layers when
    /// none match, so a non-standard PDK still produces geometry).
    /// </summary>
    public const int DefaultSiliconLayer = 1;

    private readonly NazcaComponentPreviewService _preview;
    private readonly GdsFactoryComponentPreviewService? _gdsFactoryPreview;
    private readonly double _portWidthUm;
    private readonly int _siliconLayer;

    public ComponentFdtdRequestFactory(
        NazcaComponentPreviewService preview,
        GdsFactoryComponentPreviewService? gdsFactoryPreview = null,
        double portWidthUm = DefaultPortWidthUm,
        int siliconLayer = DefaultSiliconLayer)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _gdsFactoryPreview = gdsFactoryPreview;
        _portWidthUm = portWidthUm;
        _siliconLayer = siliconLayer;
    }

    /// <summary>
    /// Renders the component and builds an FDTD request. Never fails silently: every failure
    /// throws an <see cref="InvalidOperationException"/> with a user-actionable message that
    /// the dialog shows in its solver status.
    /// </summary>
    public async Task<FdtdSMatrixRequest?> BuildAsync(Component component, CancellationToken ct = default)
    {
        var preview = await RenderComponentAsync(component, ct);
        if (!preview.Success)
            throw new InvalidOperationException(DescribeRenderFailure(preview.Error));
        if (preview.Polygons.Count == 0)
            throw new InvalidOperationException(
                "The component's geometry render returned no polygons — FDTD has nothing to simulate. " +
                "Check the component's preview (Edit Component → Preview).");
        if (preview.Pins.Count == 0)
            throw new InvalidOperationException(
                "The component's geometry render returned no ports/pins — FDTD needs at least one port. " +
                "Check the component's preview (Edit Component → Preview).");

        var componentPinNames = component.PhysicalPins?.Select(p => p.Name).ToList() ?? new List<string>();

        // Sweep the wavelengths the component is ALREADY defined at (e.g. SiEPIC's
        // 980/1310/1550 nm), not a fixed 1.5–1.6 µm band — otherwise the recompute
        // overwrites only its own range and leaves the others stale (#582).
        var sweep = FdtdWavelengthPlanner.Plan(component.WaveLengthToSMatrixMap.Keys);

        return BuildFromPreview(preview, componentPinNames, _siliconLayer, _portWidthUm, sweep);
    }

    /// <summary>
    /// Renders with the backend that owns the geometry: gdsfactory-native components via the
    /// gdsfactory renderer (their Nazca name is only a synthesized "nazca_&lt;name&gt;"
    /// placeholder no Nazca script can render), everything else via Nazca.
    /// </summary>
    private Task<NazcaPreviewResult> RenderComponentAsync(Component component, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(component.GdsFactoryFunction))
        {
            // Render the ACTUAL parametrised geometry (e.g. length=3.5), not the function's
            // defaults — otherwise the FDTD S-matrix is computed for the wrong shape and
            // template promotion spreads it to every instance of the type.
            return _preview.RenderAsync(
                component.NazcaModuleName, component.NazcaFunctionName, component.NazcaFunctionParameters, ct);
        }

        if (_gdsFactoryPreview is null)
            throw new InvalidOperationException(
                $"'{component.GdsFactoryFunction}' is a gdsfactory-native component, but no gdsfactory " +
                "renderer is configured for FDTD geometry export in this session.");

        var code = GdsFactoryPreviewCode.For(component.GdsFactoryFunction)
            ?? $"import gdsfactory as gf\ncomponent = gf.get_component('{component.GdsFactoryFunction}')\n";
        return _gdsFactoryPreview.RenderRawCodeAsync(code, ct);
    }

    private static string DescribeRenderFailure(string? rawError) =>
        FoundryEnvironmentErrorHint.Describe(rawError) is { } hint
            ? $"{hint} Python error: {rawError}"
            : $"Could not render this component's geometry for FDTD: {rawError ?? "unknown render error"}";

    /// <summary>
    /// Builds an <see cref="FdtdSMatrixRequest"/> from an already rendered preview, applying
    /// the same layer filtering and port mapping as <see cref="BuildAsync"/> without paying
    /// for a second render. A null <paramref name="sweep"/> keeps the default 1.5–1.6 µm band.
    /// </summary>
    public static FdtdSMatrixRequest BuildFromPreview(
        NazcaPreviewResult preview,
        IReadOnlyList<string> portNames,
        int siliconLayer = DefaultSiliconLayer,
        double portWidthUm = DefaultPortWidthUm,
        FdtdWavelengthPlan? sweep = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(portNames);

        sweep ??= FdtdWavelengthPlanner.Plan(Array.Empty<int>());
        return new FdtdSMatrixRequest
        {
            Polygons = BuildPolygons(preview.Polygons, siliconLayer),
            Ports = BuildPorts(preview.Pins, portNames, portWidthUm),
            LayerNumber = siliconLayer,
            Is3D = false, // 2D for a quick recompute; a 3D/accuracy toggle can come later
            WavelengthStart = sweep.StartUm,
            WavelengthStop = sweep.StopUm,
            WavelengthPoints = sweep.Points,
        };
    }

    /// <summary>
    /// Keeps only polygons on the optical layer (falls back to all layers when
    /// none match, so a PDK that puts its guide on another layer still renders).
    /// </summary>
    internal static IReadOnlyList<FdtdPolygon> BuildPolygons(
        IReadOnlyList<NazcaPreviewPolygon> polygons, int siliconLayer)
    {
        var onLayer = polygons.Where(p => p.Layer == siliconLayer).ToList();
        var source = onLayer.Count > 0 ? onLayer : polygons;
        return source.Select(p => new FdtdPolygon
        {
            Layer = p.Layer,
            Points = p.Vertices.Select(v => new FdtdPoint(v.X, v.Y)).ToList(),
        }).ToList();
    }

    /// <summary>
    /// Positions/angles come from the preview, but port NAMES come from the component's own
    /// pins (index-matched — a PDK's pin order matches its Nazca cell's) so the S-matrix is
    /// keyed by the names the simulator expects. On a count mismatch the preview names are
    /// kept rather than guessed, so the override reports the mismatch instead of mislabelling.
    /// </summary>
    internal static IReadOnlyList<FdtdPort> BuildPorts(
        IReadOnlyList<NazcaPreviewPin> pins, IReadOnlyList<string> componentPinNames, double portWidthUm)
    {
        bool useComponentNames = componentPinNames.Count == pins.Count;
        return pins.Select((pin, i) => new FdtdPort
        {
            Name = useComponentNames ? componentPinNames[i] : pin.Name,
            X = pin.X,
            Y = pin.Y,
            Orientation = pin.Angle,
            Width = portWidthUm,
        }).ToList();
    }
}
