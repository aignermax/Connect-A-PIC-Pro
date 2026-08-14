using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_DataAccess.Persistence;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// Roundtrip of canvas-level pin-less frozen paths (issue #856) through the
/// <see cref="ComponentGroupSerializer"/> DTO wrappers used by .lun save/load:
/// geometry, source-layer tag and pin-lessness must survive unchanged.
/// </summary>
public class CanvasFrozenPathPersistenceTests
{
    [Fact]
    public void Roundtrip_PinLessTaggedPath_PreservesGeometryAndLayerTag()
    {
        var original = new FrozenWaveguidePath
        {
            Path = RingPath(),
            StartPin = null,
            EndPin = null,
            Layer = 31,
            DataType = 5,
            WidthMicrometers = 0.8,
        };

        var dto = ComponentGroupSerializer.ToCanvasFrozenPathDto(original);
        var restored = ComponentGroupSerializer.FromCanvasFrozenPathDto(dto);

        restored.StartPin.ShouldBeNull();
        restored.EndPin.ShouldBeNull();
        restored.Layer.ShouldBe(31);
        restored.DataType.ShouldBe(5);
        restored.WidthMicrometers.ShouldBe(0.8);
        restored.Path.Segments.Count.ShouldBe(4);
        for (int i = 0; i < 4; i++)
        {
            restored.Path.Segments[i].StartPoint.ShouldBe(original.Path.Segments[i].StartPoint);
            restored.Path.Segments[i].EndPoint.ShouldBe(original.Path.Segments[i].EndPoint);
        }
    }

    [Fact]
    public void Roundtrip_UntaggedPath_KeepsNullLayerTag()
    {
        var original = new FrozenWaveguidePath
        {
            Path = RingPath(),
            StartPin = null,
            EndPin = null,
        };

        var restored = ComponentGroupSerializer.FromCanvasFrozenPathDto(
            ComponentGroupSerializer.ToCanvasFrozenPathDto(original));

        restored.Layer.ShouldBeNull();
        restored.DataType.ShouldBeNull();
    }

    private static RoutedPath RingPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));
        path.Segments.Add(new StraightSegment(10, 0, 10, 1, 90));
        path.Segments.Add(new StraightSegment(10, 1, 0, 1, 180));
        path.Segments.Add(new StraightSegment(0, 1, 0, 0, -90));
        return path;
    }
}
