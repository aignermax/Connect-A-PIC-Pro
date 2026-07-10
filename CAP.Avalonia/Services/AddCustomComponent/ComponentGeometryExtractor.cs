using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Rendering seam consumed by <see cref="ComponentGeometryExtractor"/>. Mirrors the two
/// preview entry points of <see cref="NazcaComponentPreviewService"/>
/// (<see cref="NazcaComponentPreviewService.RenderAsync"/> and
/// <see cref="NazcaComponentPreviewService.RenderRawCodeAsync"/>). Exists because
/// <see cref="GdsFactoryComponentPreviewService"/> is <c>sealed</c>, so Moq cannot generate
/// a mocking proxy for it directly — this interface gives the extractor a mockable
/// dependency instead.
/// </summary>
public interface IComponentPreviewRenderer
{
    /// <summary>Renders a component from a module/function reference ("module mode").</summary>
    Task<NazcaPreviewResult> RenderAsync(string? module, string function, string? parameters, CancellationToken ct = default);

    /// <summary>Renders a component from raw Python cell code ("raw-code mode").</summary>
    Task<NazcaPreviewResult> RenderRawCodeAsync(string code, CancellationToken ct = default);
}

/// <summary>
/// Thin <see cref="IComponentPreviewRenderer"/> adapter over a concrete
/// <see cref="NazcaComponentPreviewService"/> (or its <see cref="GdsFactoryComponentPreviewService"/>
/// subtype). Carries no state of its own — it only forwards to the wrapped service.
/// </summary>
public sealed class ComponentPreviewRendererAdapter : IComponentPreviewRenderer
{
    private readonly NazcaComponentPreviewService _service;

    /// <summary>Wraps the given preview service.</summary>
    public ComponentPreviewRendererAdapter(NazcaComponentPreviewService service)
    {
        _service = service;
    }

    /// <inheritdoc/>
    public Task<NazcaPreviewResult> RenderAsync(string? module, string function, string? parameters, CancellationToken ct = default)
        => _service.RenderAsync(module, function, parameters, ct);

    /// <inheritdoc/>
    public Task<NazcaPreviewResult> RenderRawCodeAsync(string code, CancellationToken ct = default)
        => _service.RenderRawCodeAsync(code, ct);
}

/// <summary>Result of rendering a geometry reference: bounding-box size + extracted pins.</summary>
/// <param name="Success">Whether the render succeeded.</param>
/// <param name="Error">Error description when <see cref="Success"/> is false.</param>
/// <param name="WidthUm">Bounding-box width in micrometers.</param>
/// <param name="HeightUm">Bounding-box height in micrometers.</param>
/// <param name="Pins">Extracted physical pins, empty when <see cref="Success"/> is false.</param>
/// <param name="Raw">The underlying preview result, for callers that need polygons/source too.</param>
public sealed record GeometryExtractResult(
    bool Success, string? Error, double WidthUm, double HeightUm,
    IReadOnlyList<OverridePinData> Pins, NazcaPreviewResult Raw);

/// <summary>
/// Renders a <see cref="GeometryReference"/> to geometry via the appropriate preview
/// renderer (nazca in module mode, gdsfactory via a raw-code wrapper) and extracts the
/// bounding-box size and physical pins — the same extraction the per-instance override
/// "Apply" performs (see <see cref="OverridePinMapper.BuildOverridePins"/>).
/// </summary>
public sealed class ComponentGeometryExtractor
{
    private readonly IComponentPreviewRenderer _nazca;
    private readonly IComponentPreviewRenderer _gdsFactory;

    /// <summary>Creates the extractor from the two backend-specific preview renderers.</summary>
    public ComponentGeometryExtractor(IComponentPreviewRenderer nazcaPreview, IComponentPreviewRenderer gdsFactoryPreview)
    {
        _nazca = nazcaPreview;
        _gdsFactory = gdsFactoryPreview;
    }

    /// <summary>Renders the reference and extracts size + pins. On render failure, Success is false.</summary>
    public async Task<GeometryExtractResult> ExtractAsync(GeometryReference reference, CancellationToken ct = default)
    {
        NazcaPreviewResult preview = await RenderAsync(reference, ct);

        if (!preview.Success)
            return new GeometryExtractResult(false, preview.Error, 0, 0, Array.Empty<OverridePinData>(), preview);

        double width = preview.XMax - preview.XMin;
        double height = preview.YMax - preview.YMin;
        var pins = OverridePinMapper.BuildOverridePins(preview);
        return new GeometryExtractResult(true, null, width, height, pins, preview);
    }

    /// <summary>
    /// Picks the backend-appropriate renderer and, when <see cref="GeometryReference.RawSourceCode"/>
    /// is set, dispatches its verbatim contents to that renderer's raw-code entry point instead of
    /// the module/function ("v1") path. Rawcode mode never synthesizes an import/wrapper — the
    /// pasted code is expected to define <c>component</c> itself.
    /// </summary>
    private Task<NazcaPreviewResult> RenderAsync(GeometryReference reference, CancellationToken ct)
    {
        IComponentPreviewRenderer renderer = reference.Backend == GeometryBackend.GdsFactory ? _gdsFactory : _nazca;

        if (reference.RawSourceCode is not null)
            return renderer.RenderRawCodeAsync(reference.RawSourceCode, ct);

        return reference.Backend == GeometryBackend.GdsFactory
            ? _gdsFactory.RenderRawCodeAsync(reference.ToGdsFactoryRawCode(), ct)
            : _nazca.RenderAsync(reference.Module, reference.Function, reference.Parameters, ct);
    }
}
