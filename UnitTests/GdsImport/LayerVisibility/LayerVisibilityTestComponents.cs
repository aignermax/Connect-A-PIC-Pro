using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;

namespace UnitTests.GdsImport.LayerVisibility;

/// <summary>
/// Builders for the layer-visibility tests: components carrying imported outline
/// polygons on given GDS (layer, datatype) pairs, and tagged frozen paths.
/// </summary>
internal static class LayerVisibilityTestComponents
{
    /// <summary>A pin-less component with one unit-square outline polygon per given pair.</summary>
    public static Component CreateWithOutlines(params (int Layer, int DataType)[] pairs)
    {
        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "nazca_outlines",
            nazcaFunctionParams: "",
            parts: new Part[1, 1] { { new Part() } },
            typeNumber: 0,
            identifier: $"outlines_{Guid.NewGuid():N}",
            rotationCounterClock: DiscreteRotation.R0,
            physicalPins: new List<PhysicalPin>())
        {
            WidthMicrometers = 10,
            HeightMicrometers = 10,
            OutlinePolygons = pairs.Select(p => UnitSquare(p.Layer, p.DataType)).ToList()
        };
    }

    /// <summary>A pin-less frozen route outline, optionally tagged with its source pair.</summary>
    public static FrozenWaveguidePath CreateFrozenPath(int? layer, int? dataType)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));
        return new FrozenWaveguidePath
        {
            Path = path,
            StartPin = null,
            EndPin = null,
            Layer = layer,
            DataType = dataType,
        };
    }

    private static OutlinePolygon UnitSquare(int layer, int dataType) => new()
    {
        Layer = layer,
        DataType = dataType,
        Points = new[]
        {
            new OutlinePoint(0, 0), new OutlinePoint(1, 0),
            new OutlinePoint(1, 1), new OutlinePoint(0, 1),
            new OutlinePoint(0, 0)
        }
    };
}
