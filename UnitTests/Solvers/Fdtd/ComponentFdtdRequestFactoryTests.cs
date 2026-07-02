using System;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Moq;
using Shouldly;
using UnitTests;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

/// <summary>
/// Verifies the geometry/port mapping the FDTD request factory applies to a
/// Nazca preview render (layer filtering and pin → port mapping).
/// </summary>
public class ComponentFdtdRequestFactoryTests
{
    private static NazcaPreviewPolygon Poly(int layer) => new()
    {
        Layer = layer,
        Vertices = new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0) },
    };

    [Fact]
    public void BuildPolygons_KeepsOnlyOpticalLayer()
    {
        var polys = new[] { Poly(1), Poly(1), Poly(20), Poly(1003) };

        var result = ComponentFdtdRequestFactory.BuildPolygons(polys, siliconLayer: 1);

        result.Count.ShouldBe(2);
        result.ShouldAllBe(p => p.Layer == 1);
    }

    [Fact]
    public void BuildPolygons_FallsBackToAllLayers_WhenNoneMatch()
    {
        var polys = new[] { Poly(2), Poly(501) };

        var result = ComponentFdtdRequestFactory.BuildPolygons(polys, siliconLayer: 1);

        result.Count.ShouldBe(2); // no layer-1 polygons → keep everything rather than render nothing
    }

    [Fact]
    public void BuildPorts_UsesComponentPinNames_KeepsPreviewPositions()
    {
        var pins = new[]
        {
            new NazcaPreviewPin { Name = "a0", X = 0, Y = 0, Angle = 180 },
            new NazcaPreviewPin { Name = "b0", X = 80, Y = 2, Angle = 0 },
        };

        // Component pin names differ from the Nazca cell pin names — these must win,
        // matched by index, while positions/angles stay from the preview.
        var ports = ComponentFdtdRequestFactory.BuildPorts(pins, new[] { "port 1", "port 2" }, portWidthUm: 2.0);

        ports.Count.ShouldBe(2);
        ports[0].Name.ShouldBe("port 1");
        ports[0].Orientation.ShouldBe(180);
        ports[0].X.ShouldBe(0);
        ports[1].Name.ShouldBe("port 2");
        ports[1].X.ShouldBe(80);
        ports[1].Width.ShouldBe(2.0);
    }

    [Fact]
    public void BuildPorts_FallsBackToPreviewNames_OnCountMismatch()
    {
        var pins = new[]
        {
            new NazcaPreviewPin { Name = "a0", X = 0, Y = 0, Angle = 180 },
            new NazcaPreviewPin { Name = "b0", X = 80, Y = 2, Angle = 0 },
        };

        // Only one component name for two pins → can't safely match → keep preview names.
        var ports = ComponentFdtdRequestFactory.BuildPorts(pins, new[] { "port 1" }, portWidthUm: 2.0);

        ports[0].Name.ShouldBe("a0");
        ports[1].Name.ShouldBe("b0");
    }

    [Fact]
    public async Task BuildAsync_OnClonedComponent_RendersAgainstOriginalModule_NotDemoFallback()
    {
        // Regression for the copy/paste FDTD failure: a pasted (cloned) component used to lose
        // its NazcaModuleName, so BuildAsync rendered against the "demo" fallback (nazca.demofab)
        // and failed with "module 'nazca.demofab' has no attribute '<cell>'". The clone must
        // carry the module so the recompute renders against the real PDK cell.
        var original = TestComponentFactory.CreateStraightWaveGuide();
        original.NazcaModuleName = "siepic_ebeam_pdk";
        original.NazcaFunctionName = "ebeam_crossing4";
        var clone = (Component)original.Clone();

        string? renderedModule = "<never called>";
        var preview = new Mock<NazcaComponentPreviewService>(
            MockBehavior.Loose, "python3", "preview.py", (TimeSpan?)null, (ProcessLaunchFactory?)null) { CallBase = false };
        preview.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string?, string, string?, CancellationToken>((module, _, _, _) => renderedModule = module)
            .ReturnsAsync(new NazcaPreviewResult
            {
                Success = true,
                Polygons = new[] { Poly(1) },
                Pins = new[] { new NazcaPreviewPin { Name = "a0", X = 0, Y = 0, Angle = 0 } },
            });

        var factory = new ComponentFdtdRequestFactory(preview.Object);
        var request = await factory.BuildAsync(clone);

        renderedModule.ShouldBe("siepic_ebeam_pdk",
            "The clone must render against the original PDK module, not the nazca.demofab fallback.");
        request.ShouldNotBeNull();
    }
}
