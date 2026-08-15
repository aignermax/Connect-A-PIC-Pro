using CAP_Core;
using CAP_Core.Analysis;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;

namespace UnitTests.Analysis;

internal static class WaveguideSpacingDetectorTestHelpers
{
    internal static WaveguideConnection CreateConnectionWithSegment(
        double x1, double y1, double x2, double y2)
    {
        var (pin1, pin2) = CreatePins(x1, y1, x2, y2);
        var conn = new WaveguideConnection { StartPin = pin1, EndPin = pin2 };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        conn.RestoreCachedPath(path);
        return conn;
    }

    internal static WaveguideConnection CreateConnectionWithSegments(params StraightSegment[] segments)
    {
        var first = segments[0];
        var last = segments[^1];
        var (pin1, pin2) = CreatePins(
            first.StartPoint.X, first.StartPoint.Y,
            last.EndPoint.X, last.EndPoint.Y);

        var conn = new WaveguideConnection { StartPin = pin1, EndPin = pin2 };
        var path = new RoutedPath();
        foreach (var segment in segments)
            path.Segments.Add(segment);
        conn.RestoreCachedPath(path);
        return conn;
    }

    internal static (PhysicalPin Start, PhysicalPin End) CreatePins(
        double x1, double y1, double x2, double y2)
    {
        var comp1 = TestComponentFactory.CreateStraightWaveGuide();
        comp1.PhysicalX = x1;
        comp1.PhysicalY = y1;
        var pin1 = new PhysicalPin
        {
            Name = "out",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            ParentComponent = comp1
        };
        comp1.PhysicalPins.Add(pin1);

        var comp2 = TestComponentFactory.CreateStraightWaveGuide();
        comp2.PhysicalX = x2;
        comp2.PhysicalY = y2;
        var pin2 = new PhysicalPin
        {
            Name = "in",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            ParentComponent = comp2
        };
        comp2.PhysicalPins.Add(pin2);

        return (pin1, pin2);
    }

    internal static ComponentGroup CreateGroupWithFrozenPath(
        double x1, double y1, double x2, double y2)
    {
        var group = new ComponentGroup("TestGroup");
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));

        var pin1 = new PhysicalPin
        {
            Name = "p1",
            ParentComponent = group,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0
        };
        var pin2 = new PhysicalPin
        {
            Name = "p2",
            ParentComponent = group,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0
        };

        group.InternalPaths.Add(new FrozenWaveguidePath
        {
            Path = path,
            StartPin = pin1,
            EndPin = pin2,
            WidthMicrometers = 0.5
        });
        return group;
    }
}
