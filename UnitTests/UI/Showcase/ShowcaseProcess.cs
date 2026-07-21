using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;

namespace UnitTests.UI.Showcase;

/// <summary>
/// Shared showcase fixtures: a realistic SiN fabrication process and a geometry-renderer
/// stub whose polygon set draws a real 1x2-MMI device shape — so the editor preview shows
/// actual geometry without needing a Python environment in the test host.
/// </summary>
internal static class ShowcaseProcess
{
    /// <summary>A realistic SiN process: layer stack, optical + metal cross-sections, materials.</summary>
    public static ProcessDefinition CreateSinProcess() => new()
    {
        Name = "Cornerstone SiN 300nm",
        Foundry = "Cornerstone",
        Version = "MPW-2026a",
        CoreThicknessNm = 300,
        Layers = new List<ProcessLayer>
        {
            new() { Name = "SiN_Core", Layer = 203, Datatype = 0, Description = "Silicon-nitride waveguide core" },
            new() { Name = "SiN_Slab", Layer = 204, Datatype = 0, Description = "Partially etched slab" },
            new() { Name = "Heater", Layer = 39, Datatype = 0, Description = "TiN thermo-optic heater" },
            new() { Name = "Metal_DC", Layer = 41, Datatype = 0, Description = "DC routing metal" },
        },
        Xsections = new List<ProcessXsection>
        {
            new() { Name = "strip_sin", Kind = XsectionKind.Optical, WidthUm = 1.2,
                    MinRadiusUm = 30, RecommendedRadiusUm = 50, Description = "SiN strip waveguide" },
            new() { Name = "rib_sin", Kind = XsectionKind.Optical, WidthUm = 0.9,
                    MinRadiusUm = 45, RecommendedRadiusUm = 80, Description = "Shallow-etch rib" },
            new() { Name = "metal_dc", Kind = XsectionKind.Metal, WidthUm = 10, Description = "DC metal line" },
        },
        Materials = new List<ProcessMaterial>
        {
            new() { Name = "SiN", Role = "core", NByWavelengthNm = new() { [1550] = 1.996 } },
            new() { Name = "SiO2", Role = "cladding", NByWavelengthNm = new() { [1550] = 1.444 } },
            new() { Name = "TiN", Role = "metal" },
        },
    };

    /// <summary>Renderer stub returning a 1x2-MMI polygon set (tapers + multimode body).</summary>
    public static IComponentPreviewRenderer MmiPreviewRenderer()
    {
        static NazcaPreviewPolygon Poly(params (double X, double Y)[] v) =>
            new() { Layer = 1, Vertices = v };
        var result = new NazcaPreviewResult
        {
            Success = true,
            XMin = 0, YMin = -3.2, XMax = 32, YMax = 3.2,
            Polygons = new List<NazcaPreviewPolygon>
            {
                Poly((0, -0.7), (0, 0.7), (6, 1.4), (6, -1.4)),          // input taper
                Poly((6, -3.0), (6, 3.0), (26, 3.0), (26, -3.0)),        // MMI body
                Poly((26, 0.55), (26, 2.45), (32, 2.2), (32, 0.8)),      // output taper (top)
                Poly((26, -2.45), (26, -0.55), (32, -0.8), (32, -2.2)),  // output taper (bottom)
            },
            Pins = new List<NazcaPreviewPin>
            {
                new() { Name = "o1", X = 0, Y = 0, Angle = 180 },
                new() { Name = "o2", X = 32, Y = 1.5, Angle = 0 },
                new() { Name = "o3", X = 32, Y = -1.5, Angle = 0 },
            },
        };
        var mock = new Mock<IComponentPreviewRenderer>();
        mock.Setup(r => r.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(result);
        mock.Setup(r => r.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock.Object;
    }
}
