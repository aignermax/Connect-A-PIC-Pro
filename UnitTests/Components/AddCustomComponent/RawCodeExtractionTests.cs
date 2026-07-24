using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Export;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class RawCodeExtractionTests
{
    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin>
        { new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 }, new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 } }
    };

    [Fact]
    public async Task RawCode_gdsfactory_renders_code_verbatim()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync("component = gf.components.mmi1x2()", It.IsAny<CancellationToken>()))
           .ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var res = await extractor.ExtractAsync(GeometryReference.RawCode(GeometryBackend.GdsFactory, "component = gf.components.mmi1x2()"));

        res.Success.ShouldBeTrue();
        res.WidthUm.ShouldBe(8);
        res.Pins.Count.ShouldBe(2);
        gds.Verify(g => g.RenderRawCodeAsync("component = gf.components.mmi1x2()", It.IsAny<CancellationToken>()), Times.Once);
        gds.Verify(g => g.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RawCode_nazca_renders_code_verbatim()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        nazca.Setup(n => n.RenderRawCodeAsync("component = nd.Cell()", It.IsAny<CancellationToken>()))
             .ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var res = await extractor.ExtractAsync(GeometryReference.RawCode(GeometryBackend.Nazca, "component = nd.Cell()"));

        res.Success.ShouldBeTrue();
        res.Pins.Count.ShouldBe(2);
        nazca.Verify(n => n.RenderRawCodeAsync("component = nd.Cell()", It.IsAny<CancellationToken>()), Times.Once);
        nazca.Verify(n => n.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RawCode_render_failure_surfaces_error_and_no_pins()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new NazcaPreviewResult { Success = false, Error = "boom" });
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var res = await extractor.ExtractAsync(GeometryReference.RawCode(GeometryBackend.GdsFactory, "component = broken()"));

        res.Success.ShouldBeFalse();
        res.Error.ShouldBe("boom");
        res.Pins.Count.ShouldBe(0);
    }
}
