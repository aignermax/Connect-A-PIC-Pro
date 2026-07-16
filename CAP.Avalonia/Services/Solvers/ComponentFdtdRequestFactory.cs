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

    /// <summary>Initializes the factory.</summary>
    /// <param name="preview">Nazca single-component renderer.</param>
    /// <param name="gdsFactoryPreview">gdsfactory renderer for gdsfactory-native components
    /// (a component with <see cref="Component.GdsFactoryFunction"/> cannot render via Nazca —
    /// its Nazca name is only a synthesized "nazca_&lt;name&gt;" placeholder).</param>
    /// <param name="portWidthUm">Port (waveguide) width in µm.</param>
    /// <param name="siliconLayer">GDS layer carrying the optical waveguide.</param>
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
    /// Renders the component and builds an FDTD request. Never fails silently: every
    /// failure (render error, no polygons, no pins) throws an
    /// <see cref="InvalidOperationException"/> whose message is user-actionable —
    /// the dialog shows it in its solver status.
    /// </summary>
    /// <exception cref="InvalidOperationException">The geometry could not be obtained.</exception>
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
    /// Renders the component with the backend that actually owns its geometry:
    /// gdsfactory-native components (CornerStone etc., non-empty
    /// <see cref="Component.GdsFactoryFunction"/>) via the gdsfactory renderer using the
    /// same import + PDK.activate() + gf.get_component() code as the canvas preview
    /// (<see cref="GdsFactoryPreviewCode"/>); everything else via the Nazca renderer.
    /// Rendering a gdsfactory component through Nazca always failed ("module
    /// 'nazca.demofab' has no attribute 'nazca_&lt;name&gt;'") — the root cause of the
    /// "Recalculate S-matrix does nothing useful" field report.
    /// </summary>
    private Task<NazcaPreviewResult> RenderComponentAsync(Component component, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(component.GdsFactoryFunction))
        {
            // Render the ACTUAL parametrised geometry (e.g. length=3.5), not the function's
            // defaults — otherwise the FDTD S-matrix is computed for the wrong shape, and #580 E's
            // template promotion would spread that default-geometry matrix to every instance of a
            // parametrised type (#580 review). Every other RenderAsync call site passes params too.
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

    /// <summary>
    /// Builds the user-facing message for a failed geometry render: a foundry-package
    /// hint (plus the raw Python error, so nothing is lost) when the failure is a
    /// recognised missing/outdated-PDK problem, otherwise the raw error prefixed with
    /// what was being attempted.
    /// </summary>
    private static string DescribeRenderFailure(string? rawError) =>
        FoundryEnvironmentErrorHint.Describe(rawError) is { } hint
            ? $"{hint} Python error: {rawError}"
            : $"Could not render this component's geometry for FDTD: {rawError ?? "unknown render error"}";

    /// <summary>
    /// Builds a complete <see cref="FdtdSMatrixRequest"/> directly from an already
    /// rendered <see cref="NazcaPreviewResult"/>, without re-rendering. Used by flows
    /// that already hold a preview render (e.g. the custom-PDK "own component" editor)
    /// so they don't pay for a second Nazca render just to get an FDTD request.
    /// Polygons are filtered to <paramref name="siliconLayer"/> (falling back to all
    /// layers when none match) and ports are index-matched to <paramref name="portNames"/>
    /// (falling back to the preview's own pin names on a count mismatch) — the same
    /// mapping <see cref="BuildAsync"/> applies.
    /// </summary>
    /// <param name="preview">Rendered geometry/pins to build the request from.</param>
    /// <param name="portNames">
    /// Port names to assign, index-matched to <paramref name="preview"/>'s pins
    /// (e.g. the component's own pin names, so the S-matrix is keyed as expected).
    /// </param>
    /// <param name="siliconLayer">GDS layer carrying the optical waveguide.</param>
    /// <param name="portWidthUm">Port (waveguide) width in µm.</param>
    /// <param name="sweep">
    /// Wavelength sweep to run; null keeps the request's default 1.5–1.6 µm band
    /// (used by flows without a placed component, e.g. the custom-PDK preview).
    /// </param>
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
    /// Builds FDTD ports from the rendered Nazca pin stubs. Positions and angles
    /// come from the preview (same coordinate frame as the polygons), but the port
    /// <b>names</b> come from the component's own pins so the resulting S-matrix is
    /// keyed by the names the simulator expects (e.g. "port 1"), not the Nazca cell
    /// pin names ("a0"/"pin1"). Without this the override can't be mapped onto the
    /// component and every wavelength is skipped.
    ///
    /// Pins are matched by index (a PDK's pin order matches its Nazca cell's pin
    /// order). If the counts differ we keep the preview names rather than guess —
    /// the override will then report the mismatch instead of mislabelling ports.
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
