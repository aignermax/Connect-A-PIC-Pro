using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Export;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="ComponentGeometryExtractor"/>: gdsfactory references render via the
/// raw-code wrapper, nazca references render via module/function dispatch, and a failed
/// render surfaces the error without pins.
/// </summary>
public class ComponentGeometryExtractorTests
{
    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 12, YMax = 3,
        Polygons = new List<NazcaPreviewPolygon>(),
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 1.5, Angle = 180 },
            new() { Name = "o2", X = 12, Y = 1.5, Angle = 0 },
        }
    };

    [Fact]
    public async Task GdsFactory_reference_renders_via_raw_code_wrapper()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.Is<string>(s => s.Contains("cspdk.sin300.coupler")), It.IsAny<CancellationToken>()))
           .ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var result = await extractor.ExtractAsync(
            new GeometryReference(GeometryBackend.GdsFactory, "cspdk.sin300", "coupler", null));

        result.Success.ShouldBeTrue();
        result.WidthUm.ShouldBe(12);
        result.HeightUm.ShouldBe(3);
        result.Pins.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Nazca_reference_renders_via_module_function()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        nazca.Setup(n => n.RenderAsync("mymod", "mycell", null, It.IsAny<CancellationToken>()))
             .ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var result = await extractor.ExtractAsync(
            new GeometryReference(GeometryBackend.Nazca, "mymod", "mycell", null));

        result.Success.ShouldBeTrue();
        result.Pins.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Failed_render_surfaces_error_and_no_pins()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new NazcaPreviewResult { Success = false, Error = "boom" });
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var result = await extractor.ExtractAsync(
            new GeometryReference(GeometryBackend.GdsFactory, "m", "f", null));

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("boom");
        result.Pins.Count.ShouldBe(0);
    }
}
