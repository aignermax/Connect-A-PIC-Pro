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
        NazcaPreviewResult preview = reference.Backend == GeometryBackend.GdsFactory
            ? await _gdsFactory.RenderRawCodeAsync(reference.ToGdsFactoryRawCode(), ct)
            : await _nazca.RenderAsync(reference.Module, reference.Function, reference.Parameters, ct);

        return ToResult(preview);
    }

    /// <summary>
    /// Renders user-authored raw cell code on the given backend and extracts size + pins —
    /// the raw-code authoring path of issue #701, reusing the same render pipeline the
    /// per-instance override editor (#559/#637) uses. On render failure, Success is false.
    /// </summary>
    public async Task<GeometryExtractResult> ExtractRawCodeAsync(
        GeometryBackend backend, string rawCode, CancellationToken ct = default)
    {
        var renderer = backend == GeometryBackend.GdsFactory ? _gdsFactory : _nazca;
        var preview = await renderer.RenderRawCodeAsync(rawCode, ct);
        return ToResult(preview);
    }

    /// <summary>Maps a raw preview result to the extractor's size/pins result shape.</summary>
    private static GeometryExtractResult ToResult(NazcaPreviewResult preview)
    {
        if (!preview.Success)
            return new GeometryExtractResult(false, preview.Error, 0, 0, Array.Empty<OverridePinData>(), preview);

        double width = preview.XMax - preview.XMin;
        double height = preview.YMax - preview.YMin;
        var pins = OverridePinMapper.BuildOverridePins(preview);
        return new GeometryExtractResult(true, null, width, height, pins, preview);
    }
}
