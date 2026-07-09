using System.Collections.Generic;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Verifies <see cref="ComponentFdtdRequestFactory.BuildFromPreview"/> builds a
/// complete FDTD request straight from a <see cref="NazcaPreviewResult"/>, without
/// re-rendering — the flow used by the "own component" custom-PDK path, which
/// already has a preview render in hand.
/// </summary>
public class PreviewFdtdRequestTests
{
    private static NazcaPreviewResult TwoPortPreview() => new()
    {
        Success = true,
        XMin = 0, YMin = 0, XMax = 10, YMax = 2,
        Polygons = new List<NazcaPreviewPolygon>
        {
            new() { Layer = 1, Vertices = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 2), (0, 2) } },
        },
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 1, Angle = 180 },
            new() { Name = "o2", X = 10, Y = 1, Angle = 0 },
        },
    };

    [Fact]
    public void BuildFromPreview_keeps_layer1_polygons_and_named_ports()
    {
        var req = ComponentFdtdRequestFactory.BuildFromPreview(TwoPortPreview(), new[] { "o1", "o2" });

        req.Polygons.Count.ShouldBe(1);
        req.Ports.Count.ShouldBe(2);
    }

    [Fact]
    public void BuildFromPreview_maps_port_names_positions_and_orientation()
    {
        var req = ComponentFdtdRequestFactory.BuildFromPreview(TwoPortPreview(), new[] { "o1", "o2" });

        req.Ports[0].Name.ShouldBe("o1");
        req.Ports[0].X.ShouldBe(0);
        req.Ports[0].Y.ShouldBe(1);
        req.Ports[0].Orientation.ShouldBe(180);
        req.Ports[1].Name.ShouldBe("o2");
        req.Ports[1].X.ShouldBe(10);
    }

    [Fact]
    public void BuildFromPreview_sets_layer_number_and_uses_2D()
    {
        var req = ComponentFdtdRequestFactory.BuildFromPreview(TwoPortPreview(), new[] { "o1", "o2" }, siliconLayer: 1);

        req.LayerNumber.ShouldBe(1);
        req.Is3D.ShouldBeFalse();
    }
}
